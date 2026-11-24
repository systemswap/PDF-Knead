using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Crafted ICC profiles against the ways that clean or rewrite profiles (M6 drafts n-icc-7, t-icc-1014-1016, u-icc-1017-1031-F, security;
/// docs/FEHLERGEDAECHTNIS.md, No. 1122): overlapping tags, duplicate entries, mluc records with sizes and offsets from 2^31 on.
/// </summary>
public sealed class IccCraftedProfileTests
{
    /// <summary>A profile with the tags in order; entries whose data are the same array share the bytes (one offset).</summary>
    private static byte[] Profile(byte version, params (string Sig, byte[] Data)[] tags)
    {
        var table = 128 + 4 + 12 * tags.Length;
        var placed = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
        var body = new MemoryStream();
        var entries = new List<(string Sig, int Offset, int Size)>();
        foreach (var (sig, data) in tags)
        {
            if (!placed.TryGetValue(data, out var at))
            {
                while (body.Length % 4 != 0) body.WriteByte(0);
                at = table + (int)body.Length;
                body.Write(data);
                placed[data] = at;
            }
            entries.Add((sig, at, data.Length));
        }
        while (body.Length % 4 != 0) body.WriteByte(0);
        var d = new byte[table + body.Length];
        body.ToArray().CopyTo(d, table);
        BinaryPrimitives.WriteUInt32BigEndian(d, (uint)d.Length);
        d[8] = version;
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(12), 0x6D6E7472);           // mntr
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(16), IccProfile.RgbSpace);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(20), 0x58595A20);           // PCS XYZ
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(36), 0x61637370);           // acsp
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(128), (uint)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            Encoding.ASCII.GetBytes(entries[i].Sig, d.AsSpan(132 + 12 * i, 4));
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(136 + 12 * i), (uint)entries[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(140 + 12 * i), (uint)entries[i].Size);
        }
        return d;
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        var d = new byte[20];
        Encoding.ASCII.GetBytes("XYZ ", d);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(8), (int)Math.Round(x * 65536));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(12), (int)Math.Round(y * 65536));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(16), (int)Math.Round(z * 65536));
        return d;
    }

    private static readonly byte[] Linear = [.. "curv"u8, 0, 0, 0, 0, 0, 0, 0, 0];

    private static (string, byte[])[] MatrixTags() =>
    [
        ("rXYZ", Xyz(0.4361, 0.2225, 0.0139)), ("gXYZ", Xyz(0.3851, 0.7169, 0.0971)), ("bXYZ", Xyz(0.1431, 0.0606, 0.7141)),
        ("rTRC", Linear), ("gTRC", Linear), ("bTRC", Linear), ("wtpt", Xyz(0.9642, 1, 0.8249)),
    ];

    private static byte[] Text(string text) => [.. "text"u8, 0, 0, 0, 0, .. Encoding.ASCII.GetBytes(text), 0];

    /// <summary>A multiLocalizedUnicodeType with the given records (language, country, length, offset) and data behind them.</summary>
    private static byte[] Mluc(uint recordSize, (uint Length, uint Offset)[] records, string text)
    {
        var data = Encoding.BigEndianUnicode.GetBytes(text);
        var d = new byte[16 + 12 * records.Length + data.Length];
        Encoding.ASCII.GetBytes("mluc", d);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), (uint)records.Length);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(12), recordSize);
        for (var r = 0; r < records.Length; r++)
        {
            Encoding.ASCII.GetBytes("deDE", d.AsSpan(16 + 12 * r));
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(20 + 12 * r), records[r].Length);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(24 + 12 * r), records[r].Offset);
        }
        data.CopyTo(d, 16 + 12 * records.Length);
        return d;
    }

    /// <summary>
    /// Removing the device description from a profile whose 500 tags overlap in part (each one starts 4 bytes later and runs to the end; ICC.1,
    /// 7.3.1 forbids it): the profile does not grow, the description's bytes are gone — before, each tag was copied on its own, 500 times most of
    /// the profile.
    /// </summary>
    [Fact]
    public void Metadata_OverlappingTags_ProfileDoesNotGrow()
    {
        var block = new byte[64 * 1024];
        Encoding.ASCII.GetBytes("HERSTELLERGEHEIM", block.AsSpan(8));
        var profile = Profile(2, [.. MatrixTags(), ("dmnd", block)]);
        // 500 further entries into the block, each 4 bytes later
        var at = (int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(136 + 12 * 7));
        var grown = new byte[profile.Length + 12 * 500];
        profile.AsSpan(0, 132 + 12 * 8).CopyTo(grown);
        profile.AsSpan(132 + 12 * 8).CopyTo(grown.AsSpan(132 + 12 * 508));
        BinaryPrimitives.WriteUInt32BigEndian(grown, (uint)grown.Length);
        BinaryPrimitives.WriteUInt32BigEndian(grown.AsSpan(128), 508);
        for (var i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt32BigEndian(grown.AsSpan(136 + 12 * i), BinaryPrimitives.ReadUInt32BigEndian(grown.AsSpan(136 + 12 * i)) + 6000);
        for (var i = 0; i < 500; i++)
        {
            Encoding.ASCII.GetBytes($"z{i:000}", grown.AsSpan(132 + 12 * (8 + i)));
            BinaryPrimitives.WriteUInt32BigEndian(grown.AsSpan(136 + 12 * (8 + i)), (uint)(at + 6000 + 4 * i));
            BinaryPrimitives.WriteUInt32BigEndian(grown.AsSpan(140 + 12 * (8 + i)), (uint)(block.Length - 4 * i));
        }
        var cleaned = ObjectMetadata.Icc(grown);
        Assert.NotNull(cleaned);
        Assert.True(cleaned.Length <= grown.Length, $"{cleaned.Length} > {grown.Length}");
        // the entry of the description is gone; its bytes stay only where a kept tag lies on them (here all of them, under the 500 tags)
        Assert.Equal(507u, BinaryPrimitives.ReadUInt32BigEndian(cleaned.AsSpan(128)));
        Assert.DoesNotContain("dmnd", Encoding.ASCII.GetString(cleaned, 132, 12 * 507));
    }

    /// <summary>
    /// Rewriting a version-4 matrix profile as 2.1 (PDF/A-1) whose 90 further entries all name one block of 1 MB: only the tags written are
    /// copied, each block once — before, every entry was copied (here 90 MB; with 100 entries of 32 MB about 3.2 GB).
    /// </summary>
    [Fact]
    public void AsVersion2_ManyEntriesOnOneBlock_CopiedOnce()
    {
        var block = new byte[1 << 20];
        var tags = MatrixTags().Concat(Enumerable.Range(0, 90).Select(i => ($"x{i:000}", block))).ToArray();
        var profile = Profile(4, tags);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var rewritten = CalibratedProfile.AsVersion2(profile);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotNull(rewritten);
        Assert.True(allocated < 4 << 20, $"{allocated} Byte");
    }

    /// <summary>
    /// A description as mluc whose record names an offset from 2^31 on, and one without records: the rewriting as 2.1 does not throw and gives
    /// the replacement text — before, the offset read as a negative int passed the check and the decoding threw, saving as PDF/A-1 failed.
    /// </summary>
    [Theory]
    [InlineData(0x80000000u, 1)]
    [InlineData(0u, 0)]
    public void AsVersion2_MlucOffsetFrom2Pow31OrNoRecord_ReplacementText(uint offset, int records)
    {
        var desc = Mluc(12, records == 0 ? [] : [(4, offset)], "Gr");
        var profile = Profile(4, [.. MatrixTags(), ("desc", desc), ("cprt", Text("frei"))]);
        var rewritten = CalibratedProfile.AsVersion2(profile);
        Assert.NotNull(rewritten);
        Assert.Contains("Profile", Encoding.ASCII.GetString(rewritten));
    }

    /// <summary>
    /// Blanking the notices of a profile whose copyright is an mluc with two records of the size 0x7FFFFFF0: the first record is blanked, the
    /// loop ends without an exception, the description is blanked too — before, 16 + r × size overflowed and the exception dropped the cleaning
    /// of the whole profile, its texts stayed.
    /// </summary>
    [Fact]
    public void Notices_MlucRecordSizeNear2Pow31_TextsStillBlanked()
    {
        var cprt = Mluc(0x7FFFFFF0, [(8, 40), (8, 40)], "GEHE");
        var profile = Profile(2, [.. MatrixTags(), ("cprt", cprt), ("desc", Text("FIRMENNAME"))]);
        var blank = ObjectMetadata.IccNotices(profile);
        Assert.NotNull(blank);
        var text = Encoding.ASCII.GetString(blank);
        Assert.DoesNotContain("FIRMENNAME", text);
        Assert.DoesNotContain("GEHE", Encoding.BigEndianUnicode.GetString(blank));
    }

    /// <summary>
    /// ICC.1, 7.2.8: the profiles PDF-Knead writes carry a valid date of creation (year, month 1–12, day 1–31, UTC) — before, all zero
    /// (M6 draft n-icc-7; docs/FEHLERGEDAECHTNIS.md, No. 1124).
    /// </summary>
    [Fact]
    public void OwnProfiles_ValidDateOfCreation()
    {
        foreach (var profile in new[] { CalibratedProfile.Srgb(), CalibratedProfile.Gray(null), CalibratedProfile.Rgb(null) })
        {
            int At(int i) => BinaryPrimitives.ReadUInt16BigEndian(profile.AsSpan(24 + 2 * i));
            Assert.InRange(At(0), 1900, 9999);
            Assert.InRange(At(1), 1, 12);
            Assert.InRange(At(2), 1, 31);
            Assert.InRange(At(3), 0, 23);
        }
    }

    /// <summary>
    /// ICC.1, F.2: a grey profile with PCSLAB maps its tone curve to L* = 100 × value — linear, 0.5 gives L* 50 (Y ≈ 0.184), not Y 0.5 (L* 76)
    /// (M6 drafts p-icc-92, u-icc-1017-1031-F; docs/FEHLERGEDAECHTNIS.md, No. 1124).
    /// </summary>
    [Fact]
    public void GreyProfileWithPcsLab_ValueIsLightness()
    {
        var profile = Profile(2, ("kTRC", Linear), ("wtpt", Xyz(0.9642, 1, 0.8249)));
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(16), IccProfile.GraySpace);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(20), 0x4C616220);   // PCS Lab
        var parsed = IccProfile.Parse(profile)!;
        Span<float> xyz = stackalloc float[3];
        parsed.ToXyz([0.5f], xyz);
        Assert.InRange(xyz[1], 0.17f, 0.20f);
    }
}
