using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M6, writing and cleaning colour profiles (docs/FEHLERGEDAECHTNIS.md, No. 1133): cleaning removes device and origin data from every profile
/// wherever it stands in a file, the profile ID is computed afresh, multilingual texts follow the program language, and PDF-Knead's own profiles
/// follow the target version. Profile helpers RIcc_/TIcc_/UIcc_ of the drafts r, t, u; own ones with the prefix E5_.
/// </summary>
public sealed partial class M6NormTests
{
    /// <summary>The profile ID as ICC.1 7.2.18 defines it, computed here independently: the MD5 with flags, intent and ID set to zero.</summary>
    private static byte[] E5_IccId(ReadOnlySpan<byte> profile)
    {
        var copy = profile.ToArray();
        copy.AsSpan(44, 4).Clear();
        copy.AsSpan(64, 4).Clear();
        copy.AsSpan(84, 16).Clear();
        return MD5.HashData(copy);
    }

    /// <summary>
    /// The profile with a header that tells where it comes from: created 6.5.2019 10:11:12, device maker „EPSO“ and model „M123“, device
    /// attributes with vendor bits DEADBEEFh and the medium bits 3 (transparency, matte), creator „ABCD“, the reserved bytes 100–127 set.
    /// </summary>
    private static byte[] E5_WithOrigin(byte[] profile)
    {
        var p = (byte[])profile.Clone();
        ReadOnlySpan<ushort> created = [2019, 5, 6, 10, 11, 12];
        for (var i = 0; i < 6; i++) BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(24 + 2 * i), created[i]);
        "EPSO"u8.CopyTo(p.AsSpan(48));
        "M123"u8.CopyTo(p.AsSpan(52));
        BinaryPrimitives.WriteUInt64BigEndian(p.AsSpan(56), 0xDEADBEEF_00000003);
        "ABCD"u8.CopyTo(p.AsSpan(80));
        p.AsSpan(100, 28).Fill(0x55);
        return p;
    }

    /// <summary>A tag of type text with the given words.</summary>
    private static byte[] E5_Text(string text) => [.. "text"u8, 0, 0, 0, 0, .. Encoding.ASCII.GetBytes(text), 0];

    private static bool E5_Has(byte[] data, string text) => data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;

    // ------------------------------------------------------------ cleaning: the header

    /// <summary>
    /// ICC.1 7.2.8, 7.2.12, 7.2.13, 7.2.14, 7.2.17, 7.2.19: cleaning a profile (CleanupItems.Metadata) always clears what tells its origin, not
    /// only where device description tags are present — the date of creation becomes the fixed date of the own profiles (a valid date stays in
    /// the field), device maker, model and creator become zero, of the device attributes the vendor bits go and the bits that describe the
    /// medium stay, the reserved bytes become zero. The colours stay exactly the same.
    /// </summary>
    [Fact]
    public void IccCleaning_OriginInTheHeader_ClearedAlways()
    {
        var profile = E5_WithOrigin(RIcc_MatrixProfile());
        var cleaned = ObjectMetadata.Icc(profile);
        Assert.NotNull(cleaned);
        ushort[] date = [.. Enumerable.Range(0, 6).Select(i => BinaryPrimitives.ReadUInt16BigEndian(cleaned.AsSpan(24 + 2 * i)))];
        Assert.Equal([2026, 11, 24, 0, 0, 0], date);
        Assert.True(cleaned.AsSpan(48, 12).IndexOfAnyExcept((byte)0) < 0, "maker, model, vendor attributes");
        Assert.Equal(3ul, BinaryPrimitives.ReadUInt64BigEndian(cleaned.AsSpan(56)));
        Assert.True(cleaned.AsSpan(80, 4).IndexOfAnyExcept((byte)0) < 0, "creator");
        Assert.True(cleaned.AsSpan(100, 28).IndexOfAnyExcept((byte)0) < 0, "reserved");
        RIcc_AssertSameColours(profile, cleaned);
    }

    // ------------------------------------------------------------ cleaning: the tags

    /// <summary>
    /// ICC.1 9.2.13, 9.2.44, 9.2.45, 10.19, 10.20 and 7.3.1 (private tags): cleaning removes the profile sequence (pseq) with the device
    /// descriptions copied into it, the profile sequence identifiers (psid), the date of calibration (calt), and every tag whose signature
    /// ICC.1:2022 does not register — a private one, the localised description of a platform, an obsolete device settings tag of version 2.
    /// The words they carry are gone, the colours stay.
    /// </summary>
    [Theory]
    [InlineData("pseq")]
    [InlineData("psid")]
    [InlineData("calt")]
    [InlineData("ZZZZ")]
    [InlineData("dscm")]
    [InlineData("devs")]
    public void IccCleaning_SequencesCalibrationDateAndPrivateTags_Removed(string signature)
    {
        var data = signature switch
        {
            "pseq" => UIcc_Pseq(1, TIcc_Mluc(("deDE", "HERSTELLERGEHEIM")), TIcc_MlucRaw(0, 12, [], [])),
            "psid" => [.. "psid"u8, 0, 0, 0, 0, .. TIcc_U32(1), .. TIcc_U32(20), .. TIcc_U32(16 + 60), .. Enumerable.Repeat((byte)0x5A, 16),
                .. TIcc_Mluc(("deDE", "HERSTELLERGEHEIM"))],
            "calt" => [.. "dtim"u8, 0, 0, 0, 0, 0x07, 0xE3, 0, 5, 0, 6, 0, 10, 0, 11, 0, 12],
            _ => E5_Text("HERSTELLERGEHEIM Seriennummer 4711"),
        };
        var profile = RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, [.. RIcc_MatrixTags(), (signature, data)], major: 4);
        var cleaned = ObjectMetadata.Icc(profile);
        Assert.NotNull(cleaned);
        Assert.Null(TIcc_Tag(cleaned, signature));
        Assert.False(E5_Has(cleaned, "HERSTELLERGEHEIM") || cleaned.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes("HERSTELLERGEHEIM")) >= 0);
        Assert.False(cleaned.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x07, 0xE3, 0, 5, 0, 6]) >= 0, "date of calibration");
        RIcc_AssertSameColours(profile, cleaned);
    }

    /// <summary>
    /// ICC.1 9.2 (registered tags) and 10.1 (the reserved bytes 4–7 of a tag type are zero): cleaning keeps every registered tag — the
    /// characterisation target, the chromatic adaptation, the luminance, the black point of earlier versions — byte for byte, only bytes 4–7 of
    /// a known type that were set (here in the white point) become zero, as the type demands. The colours stay.
    /// </summary>
    [Fact]
    public void IccCleaning_RegisteredTagsKept_ReservedBytesOfKnownTypesZero()
    {
        var targ = E5_Text("FOGRA39");
        var chad = (byte[])[.. "sf32"u8, 0, 0, 0, 0, .. Enumerable.Range(0, 9).SelectMany(i => TIcc_U32(i % 4 == 0 ? 0x10000u : 0))];
        var lumi = RIcc_XyzTag(80, 80, 80);
        var bkpt = RIcc_XyzTag(0.002, 0.002, 0.002);
        var wtpt = RIcc_XyzTag(0.9642, 1, 0.8249);
        BinaryPrimitives.WriteUInt32BigEndian(wtpt.AsSpan(4), 0xFFFFFFFF);
        var tags = RIcc_MatrixTags().Where(t => t.Item1 != "wtpt").Append(("wtpt", wtpt)).Concat([("targ", targ), ("chad", chad), ("lumi", lumi), ("bkpt", bkpt)]);
        var profile = RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, [.. tags], major: 4);
        var cleaned = ObjectMetadata.Icc(profile);
        Assert.NotNull(cleaned);
        foreach (var (sig, data) in new[] { ("targ", targ), ("chad", chad), ("lumi", lumi), ("bkpt", bkpt) }) Assert.Equal(data, TIcc_Tag(cleaned, sig));
        Assert.Equal(RIcc_XyzTag(0.9642, 1, 0.8249), TIcc_Tag(cleaned, "wtpt"));
        RIcc_AssertSameColours(profile, cleaned);
    }

    // ------------------------------------------------------------ the profile ID

    /// <summary>
    /// ICC.1 7.2.18 sentences 1–8: after cleaning, a profile of version 4 carries the profile ID computed afresh — the MD5 of the cleaned profile
    /// with flags, rendering intent and ID taken as zero (here with flags and intent set) — instead of 0; a profile of version 2, which has no
    /// such field, keeps zeros there. Before, every cleaned profile got 0.
    /// </summary>
    [Fact]
    public void IccCleaning_Version4_ProfileIdComputedAfresh()
    {
        var v4 = RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, [.. RIcc_MatrixTags(), ("dmnd", TIcc_Mluc(("deDE", "Geraet")))], major: 4);
        BinaryPrimitives.WriteUInt32BigEndian(v4.AsSpan(44), 0x00000002);   // not to be used on its own
        BinaryPrimitives.WriteUInt32BigEndian(v4.AsSpan(64), 1);            // relative colorimetric
        v4.AsSpan(84, 16).Fill(0x77);                                       // an ID of the profile as it was
        var cleaned = ObjectMetadata.Icc(v4)!;
        Assert.NotNull(cleaned);
        Assert.Equal(E5_IccId(cleaned), cleaned[84..100]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(cleaned.AsSpan(44)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(cleaned.AsSpan(64)));

        var v2 = ObjectMetadata.Icc(RIcc_MatrixProfile(("dmnd", E5_Text("Geraet"))))!;
        Assert.True(v2.AsSpan(84, 16).IndexOfAnyExcept((byte)0) < 0);
    }

    /// <summary>
    /// ICC.1 7.2.18: overwriting the description and copyright texts (CleanupItems.Notices) of a version-4 profile sets the profile ID of the
    /// changed profile, computed afresh, not 0.
    /// </summary>
    [Fact]
    public void IccNotices_Version4_ProfileIdComputedAfresh()
    {
        var profile = TIcc_V4WithDesc(TIcc_Mluc(("deDE", "Beschreibung")), TIcc_Mluc(("deDE", "Rechte bei")));
        profile.AsSpan(84, 16).Fill(0x77);
        var blank = ObjectMetadata.IccNotices(profile)!;
        Assert.NotNull(blank);
        Assert.Equal(E5_IccId(blank), blank[84..100]);
    }

    /// <summary>
    /// ICC.1 9.2.50: the description of the viewing conditions (vued) is free text like the description — overwriting the texts
    /// (CleanupItems.Notices) blanks it too; before, it stayed.
    /// </summary>
    [Fact]
    public void IccNotices_ViewingConditionsDescription_Blanked()
    {
        var profile = RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, [.. RIcc_MatrixTags(), ("vued", TIcc_Mluc(("deDE", "Buero im Keller")))], major: 4);
        var blank = ObjectMetadata.IccNotices(profile)!;
        Assert.NotNull(blank);
        Assert.Equal(-1, blank.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes("Buero im Keller")));
        RIcc_AssertSameColours(profile, blank);
    }

    /// <summary>
    /// ICC.1 7.2.18 (with docs/FEHLERGEDAECHTNIS.md, No. 517): the fingerprint of a profile without an ID is the ID it would have — the MD5 with
    /// flags and rendering intent taken as zero —, so a profile with flags and intent set and its copy with the ID written count as the same
    /// profile; before, the MD5 of the bytes as they are was compared and the two counted as different. Two copies whose written IDs differ stay
    /// different (the test file „pass d“ of the reference validator for ISO 19005-4, 6.2.4.2 test 3).
    /// </summary>
    [Fact]
    public void IccFingerprint_WithoutId_FlagsAndIntentTakenAsZero()
    {
        var plain = RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, RIcc_MatrixTags(), major: 4);
        BinaryPrimitives.WriteUInt32BigEndian(plain.AsSpan(44), 0x00000001);
        BinaryPrimitives.WriteUInt32BigEndian(plain.AsSpan(64), 2);
        var withId = (byte[])plain.Clone();
        E5_IccId(plain).CopyTo(withId, 84);
        Assert.Equal(E5_IccId(plain), IccProfile.Fingerprint(plain));
        Assert.True(IccProfile.SameProfile(plain, withId));
        var otherId = (byte[])withId.Clone();
        otherId[90] ^= 0x21;
        Assert.False(IccProfile.SameProfile(withId, otherId));
    }

    // ------------------------------------------------------------ cleaning: every place a profile stands

    /// <summary>The ICC profile of a JPEG put together from its APP2 segments in the order of their numbers; the segments as (number, count).</summary>
    private static (byte[] Profile, List<(int Number, int Count)> Parts) E5_JpegProfile(byte[] jpeg)
    {
        var parts = new List<(int Number, int Count, byte[] Data)>();
        for (var at = 2; at + 4 <= jpeg.Length && jpeg[at] == 0xFF && jpeg[at + 1] is not (0xDA or 0xD9);)
        {
            var length = jpeg[at + 2] << 8 | jpeg[at + 3];
            var body = jpeg.AsSpan(at + 4, length - 2);
            if (jpeg[at + 1] == 0xE2 && body.StartsWith("ICC_PROFILE\0"u8)) parts.Add((body[12], body[13], body[14..].ToArray()));
            at += 2 + length;
        }
        return ([.. parts.OrderBy(p => p.Number).SelectMany(p => p.Data)], [.. parts.Select(p => (p.Number, p.Count))]);
    }

    /// <summary>A small real JPEG (from Skia) with the profile in APP2 segments of at most <paramref name="chunk"/> bytes each.</summary>
    private static byte[] E5_JpegWithProfile(byte[] profile, int chunk)
    {
        using var bitmap = new SKBitmap(16, 16);
        bitmap.Erase(new SKColor(30, 120, 200));
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        var jpeg = encoded.ToArray();
        var count = (profile.Length + chunk - 1) / chunk;
        var segments = new List<byte>();
        for (var i = 0; i < count; i++)
        {
            byte[] body = [.. "ICC_PROFILE\0"u8, (byte)(i + 1), (byte)count, .. profile.AsSpan(i * chunk, Math.Min(chunk, profile.Length - i * chunk))];
            segments.AddRange([0xFF, 0xE2, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body]);
        }
        return [.. jpeg[..2], .. segments, .. jpeg[2..]];
    }

    /// <summary>
    /// ITU-T T.872 6.5.1 (ICC.1 Annex B.4) with ICC.1 7.2 and 9.2: a colour profile embedded in a JPEG image (APP2) is cleaned like a profile
    /// stream — before, the APP2 segments stayed as they were, with the device description, the private tags and the origin in the header. The
    /// cleaned profile is written into numbered segments again (here a profile of three segments of up to 60 000 bytes becomes one), the image
    /// data stay the same.
    /// </summary>
    [Fact]
    public void JpegIccProfile_CleanedInItsSegments()
    {
        var big = new byte[150_000];
        "text"u8.CopyTo(big);
        Encoding.ASCII.GetBytes("PRIVATGEHEIM", big.AsSpan(8));
        var profile = E5_WithOrigin(RIcc_MatrixProfile(("dmnd", E5_Text("HERSTELLERGEHEIM")), ("ZZZZ", big)));
        var file = E5_JpegWithProfile(profile, 60_000);
        Assert.Equal(3, E5_JpegProfile(file).Parts.Count);
        var cleaned = ObjectMetadata.Jpeg(file);
        Assert.NotNull(cleaned);
        var (inside, parts) = E5_JpegProfile(cleaned);
        Assert.Equal(ObjectMetadata.Icc(profile), inside);
        Assert.Equal([(1, 1)], parts);
        Assert.False(E5_Has(cleaned, "HERSTELLERGEHEIM") || E5_Has(cleaned, "PRIVATGEHEIM") || E5_Has(cleaned, "EPSO"));
        using var before = SKBitmap.Decode(file);
        using var after = SKBitmap.Decode(cleaned);
        Assert.Equal(before.Pixels, after.Pixels);
        Assert.Null(ObjectMetadata.Jpeg(cleaned));   // nothing left to clean
    }

    private static byte[] E5_Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        return [.. TIcc_U32((uint)(8 + body.Length)), .. Encoding.ASCII.GetBytes(type), .. body];
    }

    /// <summary>
    /// ISO/IEC 15444-1 I.5.3.3 (colour specification box, method 2) with ICC.1 7.2 and 9.2: a colour profile embedded in a JPEG 2000 file is
    /// cleaned like a profile stream — before, it stayed with the device description and the origin in the header. The lengths of the colour box
    /// and of the JP2 header box around it shrink with the profile, the box after them (the codestream) stays byte for byte, and the image
    /// information still finds the profile and its name.
    /// </summary>
    [Fact]
    public void JpxIccProfile_CleanedInItsColourBox()
    {
        var profile = E5_WithOrigin(RIcc_MatrixProfile(("desc", TIcc_Mluc(("enUS", "Beispielprofil"))), ("dmnd", E5_Text("HERSTELLERGEHEIM"))));
        byte[] codestream = [0xFF, 0x4F, 0xFF, 0x51, 0, 2, 1, 2, 3, 0xFF, 0xD9];
        byte[] ihdr = [.. TIcc_U32(16), .. TIcc_U32(16), 0, 3, 7, 7, 0, 0];
        var file = (byte[])[.. E5_Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]), .. E5_Box("ftyp", "jp2 "u8.ToArray(), TIcc_U32(0), "jp2 "u8.ToArray()),
            .. E5_Box("jp2h", E5_Box("ihdr", ihdr), E5_Box("colr", [2, 0, 0], profile)), .. E5_Box("jp2c", codestream)];
        var cleaned = ObjectMetadata.Jpx(file);
        Assert.NotNull(cleaned);
        var clean = ObjectMetadata.Icc(profile)!;
        var expected = (byte[])[.. E5_Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]), .. E5_Box("ftyp", "jp2 "u8.ToArray(), TIcc_U32(0), "jp2 "u8.ToArray()),
            .. E5_Box("jp2h", E5_Box("ihdr", ihdr), E5_Box("colr", [2, 0, 0], clean)), .. E5_Box("jp2c", codestream)];
        Assert.Equal(expected, cleaned);
        Assert.Equal("Beispielprofil", ImageMetadata.FromJpx(cleaned)!.IccProfile);
        Assert.Null(ObjectMetadata.Jpx(cleaned));
    }

    /// <summary>
    /// ICC.1 7.2 with ISO 32000-2 14.11.5: the profile of an output intent is cleaned when the file is saved with „Metadaten“ — also a profile
    /// larger than 4 MB (up to the 32 MB a profile may have to be read), which before was not looked at; the private tag and the origin in the
    /// header are gone, the profile reads with the same colours.
    /// </summary>
    [Fact]
    public void IccCleaning_InAFile_LargeOutputIntentProfile()
    {
        var big = new byte[5 << 20];
        "text"u8.CopyTo(big);
        Encoding.ASCII.GetBytes("PRIVATGEHEIM", big.AsSpan(8));
        var profile = E5_WithOrigin(RIcc_MatrixProfile(("ZZZZ", big)));
        var pdf = TestPdf.Simple(1);
        var stream = pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(3))), profile));
        ((PdfDictionary)pdf.Get(1)).Set(PdfName.Get("OutputIntents"), new PdfArray([TestPdf.Dict(("Type", TestPdf.N("OutputIntent")),
            ("S", TestPdf.N("GTS_PDFA1")), ("OutputConditionIdentifier", new PdfString("Beispiel"u8.ToArray())), ("DestOutputProfile", TestPdf.Ref(stream)))]));
        using var doc = PdfDocument.Open(pdf.Build());
        using var output = new MemoryStream();
        PdfWriter.Write(doc, output, new PdfSaveOptions { Cleanup = CleanupItems.Metadata });
        using var saved = PdfDocument.Open(output.ToArray());
        var intent = (PdfDictionary)saved.Catalog!.GetArray(PdfName.Get("OutputIntents"))!.Get(0)!;
        var written = saved.GetStreamData((PdfStream)intent.Get(PdfName.Get("DestOutputProfile"))!);
        Assert.Null(TIcc_Tag(written, "ZZZZ"));
        Assert.False(E5_Has(written, "PRIVATGEHEIM") || E5_Has(written, "EPSO"));
        RIcc_AssertSameColours(profile, written);
    }

    /// <summary>
    /// ICC.1 7.2 with ISO 32000-2 8.6.5.5 (Table 65): a profile stream is recognised by its use, not only by /N — a stream without /N that an
    /// ICCBased space names (here the base of an Indexed space in the resources of the page) is cleaned
    /// like any profile: the origin in the header and the private tag are gone, the description stays. Before, such a stream was not looked at.
    /// </summary>
    [Fact]
    public void IccCleaning_InAFile_ProfileWithoutN_FoundByItsUse()
    {
        var profile = E5_WithOrigin(RIcc_MatrixProfile(("desc", TIcc_Mluc(("enUS", "Beispielprofil"))), ("ZZZZ", E5_Text("PRIVATGEHEIM"))));
        var pdf = TestPdf.Simple(1);
        var stream = pdf.Add(new PdfStream(new PdfDictionary(), profile));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Resources, TestPdf.Dict(("ColorSpace", TestPdf.Dict(("CS0", new PdfArray([TestPdf.N("Indexed"),
            new PdfArray([TestPdf.N("ICCBased"), TestPdf.Ref(stream)]), PdfInteger.Of(1), new PdfString(new byte[6])]))))));
        pdf.Replace(4, TestPdf.FlateStream("/CS0 cs 1 sc 10 10 50 50 re f"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.Contains("1 Farbprofilen", Assert.Single(DocumentCleanup.Inspect(doc, PageItem.From(doc)), f => f.Item == CleanupItems.Metadata).Detail);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, output, new PdfSaveOptions { Cleanup = CleanupItems.Metadata });
        using var saved = PdfDocument.Open(output.ToArray());
        var space = (PdfArray)saved.Pages[0].Resources!.GetDictionary(PdfName.Get("ColorSpace"))!.Get(PdfName.Get("CS0"))!;
        var written = saved.GetStreamData((PdfStream)((PdfArray)space.Get(1)!).Get(1)!);
        Assert.Null(TIcc_Tag(written, "ZZZZ"));
        Assert.False(E5_Has(written, "PRIVATGEHEIM") || E5_Has(written, "EPSO") || E5_Has(written, "ABCD"));
        Assert.Equal("Beispielprofil", ImageMetadata.IccName(written, "en"));
    }

    /// <summary>
    /// ITU-T T.872 6.5.1 (ICC.1 Annex B.4): APP2 profile segments whose numbers make no profile — a number twice, or one missing — give no profile
    /// to any reader (PDF-Knead's decoder does not read them, the image is drawn in the colour space of the PDF): cleaning removes them, since they
    /// could only carry data — before, they stayed as they were with the device data in them. The image data stay the same.
    /// </summary>
    [Theory]
    [InlineData("twice")]
    [InlineData("missing")]
    public void JpegIccProfile_SegmentsMakingNoProfile_Removed(string variant)
    {
        var profile = E5_WithOrigin(RIcc_MatrixProfile(("dmnd", E5_Text("HERSTELLERGEHEIM"))));
        var whole = E5_JpegWithProfile(profile, (profile.Length + 2) / 3);
        // the segments numbered 1, 2, 3 of 3: the second renumbered 1 (twice) or the third numbered 4 (3 missing)
        var file = (byte[])whole.Clone();
        var seen = 0;
        for (var at = 2; at + 4 <= file.Length && file[at] == 0xFF && file[at + 1] != 0xDA; at += 2 + (file[at + 2] << 8 | file[at + 3]))
            if (file[at + 1] == 0xE2 && ++seen == (variant == "twice" ? 2 : 3)) file[at + 16] = variant == "twice" ? (byte)1 : (byte)4;
        var cleaned = ObjectMetadata.Jpeg(file);
        Assert.NotNull(cleaned);
        Assert.Empty(E5_JpegProfile(cleaned).Parts);
        Assert.False(E5_Has(cleaned, "HERSTELLERGEHEIM") || E5_Has(cleaned, "EPSO"));
        using var before = SKBitmap.Decode(file);
        using var after = SKBitmap.Decode(cleaned);
        Assert.Equal(before.Pixels, after.Pixels);
    }

    // ------------------------------------------------------------ required tags (ICC.1 8.2) on the PDF/A way

    /// <summary>An RGB display profile of the given major version without the required tags desc, cprt and wtpt.</summary>
    private static byte[] E5_WithoutRequiredTags(byte major) =>
        RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, [.. RIcc_MatrixTags().Where(t => t.Item1 != "wtpt")], major);

    /// <summary>
    /// ICC.1 8.2 sentence 1 with ISO 19005-2/-4 6.2.4.2 and ISO 19005-1 6.2.3.2: a profile of an ICCBased space without description, copyright
    /// and media white point is no valid profile — the check names it (rule 6.2.4.2-1 / 6.2.3.2-1); the PDF/A copy adds the missing tags
    /// without changing a colour: wtpt D50, a neutral description and copyright, as the types of its version (2: textDescriptionType and
    /// textType, 4: multiLocalizedUnicodeType with the profile ID set); for PDF/A-1 the version-4 profile is written as 2.1 with all three. The
    /// copy conforms and draws the same colours.
    /// </summary>
    [Theory]
    [InlineData(PdfAConformance.A2b, 2)]
    [InlineData(PdfAConformance.A4, 4)]
    [InlineData(PdfAConformance.A1b, 4)]
    public void PdfA_IccProfileWithoutRequiredTags_FoundAndCompleted(PdfAConformance part, byte major)
    {
        var profile = E5_WithoutRequiredTags(major);
        var pdf = TestPdf.Simple(1);
        var stream = pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(3))), profile));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("ColorSpace", TestPdf.Dict(("CS0", new PdfArray([TestPdf.N("ICCBased"), TestPdf.Ref(stream)]))))));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "/CS0 cs 0.2 0.5 0.9 sc 72 600 200 100 re f"u8.ToArray()));
        var data = pdf.Build();
        using (var doc = PdfDocument.Open(data))
            // (in PDF/A-1 the version 4 is named first: the header is not valid there)
            Assert.Contains(PdfACheck.Check(doc, part), v => v.Id == PdfARules.IccProfileValid.Id(part) &&
                (part == PdfAConformance.A1b || v.Text.Contains("Pflichtangabe")));
        var result = PdfATests.Write(data, part);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var saved = PdfDocument.Open(result.Bytes);
        var space = (PdfArray)saved.Pages[0].Resources!.GetDictionary(PdfName.Get("ColorSpace"))!.Get(PdfName.Get("CS0"))!;
        var written = saved.GetStreamData((PdfStream)space.Get(1)!);
        foreach (var sig in new[] { "desc", "cprt", "wtpt" }) Assert.NotNull(TIcc_Tag(written, sig));
        Assert.Equal(RIcc_XyzTag(0.9642, 1, 0.8249), TIcc_Tag(written, "wtpt"));
        if (written[8] >= 4)
        {
            Assert.Equal("mluc", Encoding.ASCII.GetString(TIcc_Tag(written, "desc")!, 0, 4));
            Assert.Equal("mluc", Encoding.ASCII.GetString(TIcc_Tag(written, "cprt")!, 0, 4));
            Assert.Equal(E5_IccId(written), written[84..100]);
        }
        else
        {
            Assert.Equal("desc", Encoding.ASCII.GetString(TIcc_Tag(written, "desc")!, 0, 4));
            Assert.Equal("text", Encoding.ASCII.GetString(TIcc_Tag(written, "cprt")!, 0, 4));
        }
        RIcc_AssertSameColours(profile, written);
    }

    /// <summary>
    /// ICC.1 8.2 sentence 1 with ISO 19005-2 6.2.3: the profile of the output intent without the required tags is named by the check; the PDF/A
    /// copy keeps it as the output intent with the missing tags added (same colours), and conforms.
    /// </summary>
    [Fact]
    public void PdfA_OutputIntentProfileWithoutRequiredTags_FoundAndCompleted()
    {
        var profile = E5_WithoutRequiredTags(2);
        var pdf = TestPdf.Simple(1);
        var stream = pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(3))), profile));
        ((PdfDictionary)pdf.Get(1)).Set(PdfName.Get("OutputIntents"), new PdfArray([TestPdf.Dict(("Type", TestPdf.N("OutputIntent")),
            ("S", TestPdf.N("GTS_PDFA1")), ("OutputConditionIdentifier", new PdfString("Beispiel"u8.ToArray())), ("DestOutputProfile", TestPdf.Ref(stream)))]));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "0 0 1 rg 72 600 200 100 re f"u8.ToArray()));
        var data = pdf.Build();
        using (var doc = PdfDocument.Open(data))
            Assert.Contains(PdfACheck.Check(doc, PdfAConformance.A2b), v => v.Text.Contains("Pflichtangabe"));
        var result = PdfATests.Write(data, PdfAConformance.A2b);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var saved = PdfDocument.Open(result.Bytes);
        var intent = (PdfDictionary)saved.Catalog!.GetArray(PdfName.Get("OutputIntents"))!.Get(0)!;
        Assert.Equal("Beispiel", intent.GetString(PdfName.Get("OutputConditionIdentifier"))!.ToText());
        var written = saved.GetStreamData((PdfStream)intent.Get(PdfName.Get("DestOutputProfile"))!);
        foreach (var sig in new[] { "desc", "cprt", "wtpt" }) Assert.NotNull(TIcc_Tag(written, sig));
        RIcc_AssertSameColours(profile, written);
    }

    // ------------------------------------------------------------ own profiles per target version

    /// <summary>The nine values of an s15Fixed16ArrayType.</summary>
    private static double[] E5_Sf32(byte[] tag) => [.. Enumerable.Range(0, (tag.Length - 8) / 4).Select(i => BinaryPrimitives.ReadInt32BigEndian(tag.AsSpan(8 + 4 * i)) / 65536.0)];

    /// <summary>
    /// ISO 32000-2 8.6.5.5 (Table 66: ICC.1:2010 for PDF 2.0) with ICC.1 7.2.4, 7.2.11, 7.2.18, 8.2, 9.2.15, 9.2.22, 9.2.43, 10.15: the own sRGB
    /// profile for PDF 2.0 is a profile of version 4.3 — description and copyright as mluc (en/US), the chromatic adaptation from the D65 white
    /// of sRGB to D50 (Bradford) in chad, the profile ID set, the flag „embedded“ set —, readable by PDF-Knead's own reader and allowed for
    /// PDF/A-4, with exactly the colours of the version-2 profile.
    /// </summary>
    [Fact]
    public void OwnSrgb_ForPdf20_Version4WithMlucChadAndId()
    {
        var v4 = CalibratedProfile.Srgb(version4: true);
        Assert.Equal([4, 0x30, 0, 0], v4[8..12]);
        Assert.Equal("mluc", Encoding.ASCII.GetString(TIcc_Tag(v4, "desc")!, 0, 4));
        Assert.Equal("mluc", Encoding.ASCII.GetString(TIcc_Tag(v4, "cprt")!, 0, 4));
        Assert.Equal(CalibratedProfile.SrgbName, ImageMetadata.IccName(v4, "en"));
        // the Bradford adaptation D65 → D50 (the values of the published sRGB profile of version 4)
        double[] bradford = [1.0479, 0.0229, -0.0502, 0.0296, 0.9904, -0.0171, -0.0092, 0.0151, 0.7519];
        var chad = E5_Sf32(TIcc_Tag(v4, "chad")!);
        for (var i = 0; i < 9; i++) Assert.InRange(chad[i], bradford[i] - 0.0005, bradford[i] + 0.0005);
        Assert.Equal(E5_IccId(v4), v4[84..100]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(v4.AsSpan(44)) & 1);
        Assert.Equal((uint?)IccProfile.RgbSpace, PdfACheck.UsableProfile(v4, 4));
        RIcc_AssertSameColours(CalibratedProfile.Srgb(), v4);
        Assert.Null(ObjectMetadata.Icc(v4));   // nothing to clean in an own profile
        Assert.Null(ObjectMetadata.Icc(CalibratedProfile.Srgb()));
    }

    /// <summary>
    /// ISO 32000-2 8.6.5.5 with ISO 19005-1 6.2.2 and ISO 19005-4 6.2.3: the own sRGB profile of the output intent follows the target version —
    /// version 4 in a PDF/A-4 file (PDF 2.0), version 2 in PDF/A-1 and PDF/A-2 files; each copy conforms.
    /// </summary>
    [Theory]
    [InlineData(PdfAConformance.A4, 4)]
    [InlineData(PdfAConformance.A2b, 2)]
    [InlineData(PdfAConformance.A1b, 2)]
    public void PdfA_OwnSrgbOutputProfile_VersionByTarget(PdfAConformance part, int major)
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "0 0 1 rg 72 600 200 100 re f"u8.ToArray()));
        var result = PdfATests.Write(pdf.Build(), part);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var intent = (PdfDictionary)doc.Catalog!.GetArray(PdfName.Get("OutputIntents"))!.Get(0)!;
        var profile = doc.GetStreamData((PdfStream)intent.Get(PdfName.Get("DestOutputProfile"))!);
        Assert.Equal(major, profile[8]);
    }
}
