using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M6 draft r-icc-1010-1011: the tables lut16 (ICC.1, 10.10) and lut8 (ICC.1, 10.11) as the reader evaluates them — profiles built here byte by
/// byte. The helpers of this file (prefix RIcc_) also build the profiles of the drafts t-icc-1014-1016 and u-icc-1017-1031-F.
/// </summary>
public sealed partial class M6NormTests
{
    // ------------------------------------------------------------ profile building (shared by the ICC packages r, t, u)

    internal const uint RIcc_XyzSpace = 0x58595A20, RIcc_LabSpace = 0x4C616220;

    /// <summary>A four-character signature as a number (big-endian).</summary>
    internal static uint RIcc_Sig(string s) => BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(s));

    /// <summary>
    /// An ICC profile: header (size, major version, device class, data colour space, PCS, 'acsp', D50 as illuminant) and the tags in order,
    /// each at a multiple of four bytes; entries given the same array share their bytes.
    /// </summary>
    internal static byte[] RIcc_Profile(string deviceClass, uint space, uint pcs, (string Sig, byte[] Data)[] tags, byte major = 2)
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
        d[8] = major;
        d[9] = major == 2 ? (byte)0x10 : (byte)0x30;
        Encoding.ASCII.GetBytes(deviceClass, d.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(16), space);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(20), pcs);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(36), 0x61637370);   // acsp
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(68), (int)Math.Round(IccProfile.WhiteX * 65536.0));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(72), (int)Math.Round(IccProfile.WhiteY * 65536.0));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(76), (int)Math.Round(IccProfile.WhiteZ * 65536.0));
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(128), (uint)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            Encoding.ASCII.GetBytes(entries[i].Sig, d.AsSpan(132 + 12 * i, 4));
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(136 + 12 * i), (uint)entries[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(140 + 12 * i), (uint)entries[i].Size);
        }
        return d;
    }

    /// <summary>XYZType: three s15Fixed16 numbers.</summary>
    internal static byte[] RIcc_XyzTag(double x, double y, double z)
    {
        var d = new byte[20];
        "XYZ "u8.CopyTo(d);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(8), (int)Math.Round(x * 65536));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(12), (int)Math.Round(y * 65536));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(16), (int)Math.Round(z * 65536));
        return d;
    }

    /// <summary>curveType with one entry: a pure gamma (u8Fixed8Number).</summary>
    internal static byte[] RIcc_Gamma(double gamma)
    {
        var d = new byte[14];
        "curv"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(12), (ushort)Math.Round(gamma * 256));
        return d;
    }

    /// <summary>parametricCurveType of a function type with its parameters (s15Fixed16).</summary>
    internal static byte[] RIcc_Para(int type, params double[] p)
    {
        var d = new byte[12 + 4 * p.Length];
        "para"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(8), (ushort)type);
        for (var i = 0; i < p.Length; i++) BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(12 + 4 * i), (int)Math.Round(p[i] * 65536));
        return d;
    }

    /// <summary>The tags of an RGB matrix profile with AdobeRGB-like colorants (D50) and one tone curve for all three channels.</summary>
    internal static (string, byte[])[] RIcc_MatrixTags(byte[]? trc = null)
    {
        trc ??= RIcc_Gamma(2.2);
        return
        [
            ("rXYZ", RIcc_XyzTag(0.6097559, 0.3111242, 0.0194811)), ("gXYZ", RIcc_XyzTag(0.2052401, 0.6256560, 0.0608902)),
            ("bXYZ", RIcc_XyzTag(0.1492240, 0.0632197, 0.7448387)), ("rTRC", trc), ("gTRC", trc), ("bTRC", trc),
            ("wtpt", RIcc_XyzTag(0.9642, 1, 0.8249)),
        ];
    }

    /// <summary>An RGB matrix profile (display class, PCS XYZ) with further tags.</summary>
    internal static byte[] RIcc_MatrixProfile(params (string, byte[])[] more) =>
        RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, [.. RIcc_MatrixTags(), .. more]);

    /// <summary>
    /// lut16Type: matrix (identity unless given), input curves of <paramref name="inEntries"/> points and output curves of
    /// <paramref name="outEntries"/> points (identity), the table from <paramref name="clut"/> (grid indices, first input slowest → outputs).
    /// </summary>
    internal static byte[] RIcc_Lut16(int inputs, int outputs, int grid, Func<int[], int[]> clut, double[]? matrix = null, int inEntries = 2, int outEntries = 2)
    {
        var points = 1;
        for (var i = 0; i < inputs; i++) points *= grid;
        var d = new byte[52 + 2 * inEntries * inputs + 2 * points * outputs + 2 * outEntries * outputs];
        "mft2"u8.CopyTo(d);
        d[8] = (byte)inputs;
        d[9] = (byte)outputs;
        d[10] = (byte)grid;
        matrix ??= [1, 0, 0, 0, 1, 0, 0, 0, 1];
        for (var k = 0; k < 9; k++) BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(12 + 4 * k), (int)Math.Round(matrix[k] * 65536));
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(48), (ushort)inEntries);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(50), (ushort)outEntries);
        var at = 52;
        void Curve(int n)
        {
            for (var j = 0; j < n; j++, at += 2)
                BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(at), (ushort)(n == 1 ? 0 : Math.Round(j * 65535.0 / (n - 1))));
        }
        for (var i = 0; i < inputs; i++) Curve(inEntries);
        var index = new int[inputs];
        for (var p = 0; p < points; p++)
        {
            for (int i = inputs - 1, rest = p; i >= 0; i--, rest /= grid) index[i] = rest % grid;
            var values = clut(index);
            for (var o = 0; o < outputs; o++, at += 2) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(at), (ushort)values[o]);
        }
        for (var o = 0; o < outputs; o++) Curve(outEntries);
        return d;
    }

    /// <summary>lut8Type: matrix (identity unless given), identity curves of 256 entries, the table from <paramref name="clut"/> (bytes).</summary>
    internal static byte[] RIcc_Lut8(int inputs, int outputs, int grid, Func<int[], int[]> clut, double[]? matrix = null)
    {
        var points = 1;
        for (var i = 0; i < inputs; i++) points *= grid;
        var d = new byte[48 + 256 * inputs + points * outputs + 256 * outputs];
        "mft1"u8.CopyTo(d);
        d[8] = (byte)inputs;
        d[9] = (byte)outputs;
        d[10] = (byte)grid;
        matrix ??= [1, 0, 0, 0, 1, 0, 0, 0, 1];
        for (var k = 0; k < 9; k++) BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(12 + 4 * k), (int)Math.Round(matrix[k] * 65536));
        var at = 48;
        for (var i = 0; i < inputs; i++)
            for (var j = 0; j < 256; j++) d[at++] = (byte)j;
        var index = new int[inputs];
        for (var p = 0; p < points; p++)
        {
            for (int i = inputs - 1, rest = p; i >= 0; i--, rest /= grid) index[i] = rest % grid;
            var values = clut(index);
            for (var o = 0; o < outputs; o++) d[at++] = (byte)values[o];
        }
        for (var o = 0; o < outputs; o++)
            for (var j = 0; j < 256; j++) d[at++] = (byte)j;
        return d;
    }

    /// <summary>A lut16 or lut8 (<paramref name="kind"/>) whose table entries are given in 16 bits (lut8 takes the high byte).</summary>
    internal static byte[] RIcc_Lut(string kind, int inputs, int outputs, int grid, Func<int[], int[]> clut16, double[]? matrix = null) =>
        kind == "lut16" ? RIcc_Lut16(inputs, outputs, grid, clut16, matrix)
        : RIcc_Lut8(inputs, outputs, grid, i => [.. clut16(i).Select(v => v >> 8)], matrix);

    /// <summary>A table profile with the lut as A2B0.</summary>
    internal static byte[] RIcc_TableProfile(string deviceClass, uint space, uint pcs, byte[] lut) =>
        RIcc_Profile(deviceClass, space, pcs, [("wtpt", RIcc_XyzTag(0.9642, 1, 0.8249)), ("A2B0", lut)]);

    /// <summary>Device values → XYZ (D50) of a profile.</summary>
    internal static float[] RIcc_ToXyz(IccProfile profile, params float[] device)
    {
        var xyz = new float[3];
        profile.ToXyz(device, xyz);
        return xyz;
    }

    /// <summary>Both profiles readable and giving exactly the same XYZ for every colour of a grid (0, 0.3, 0.7, 1 on each channel).</summary>
    internal static void RIcc_AssertSameColours(byte[] expected, byte[] actual)
    {
        var a = IccProfile.Parse(expected);
        var b = IccProfile.Parse(actual);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Channels, b!.Channels);
        float[] steps = [0, 0.3f, 0.7f, 1];
        var n = a.Channels;
        var count = (int)Math.Pow(steps.Length, n);
        var device = new float[n];
        for (var p = 0; p < count; p++)
        {
            for (int i = 0, rest = p; i < n; i++, rest /= steps.Length) device[i] = steps[rest % steps.Length];
            Assert.Equal(RIcc_ToXyz(a, device), RIcc_ToXyz(b, device));
        }
    }

    /// <summary>The colour space of an ICCBased stream with /N and, if given, /Alternate and /Range.</summary>
    internal static ColorSpace RIcc_IccBased(PdfDocument doc, byte[] profile, int n, string? alternate = null, double[]? range = null)
    {
        var dict = TestPdf.Dict(("N", PdfInteger.Of(n)));
        if (alternate is not null) dict.Set(PdfName.Get("Alternate"), TestPdf.N(alternate));
        if (range is not null) dict.Set(PdfName.Get("Range"), TestPdf.Box(range));
        return ColorSpace.Create(new PdfArray([TestPdf.N("ICCBased"), new PdfStream(dict, profile)]), null, doc);
    }

    /// <summary>The lut16 tags (mft2) of the bundled CMYK profile: signature, offset and size, each table once.</summary>
    private static List<(string Sig, int Offset, int Size)> RIcc_FograMft2()
    {
        var data = CmykProfile.Bytes();
        var result = new List<(string, int, int)>();
        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(128));
        for (var i = 0; i < count; i++)
        {
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(136 + 12 * i));
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(140 + 12 * i));
            if (data.AsSpan(offset, 4).SequenceEqual("mft2"u8)) result.Add((Encoding.ASCII.GetString(data, 132 + 12 * i, 4), offset, size));
        }
        return result;
    }

    private static double[] RIcc_MatrixCase(string matrix) => matrix switch
    {
        "e1half" => [0.5, 0, 0, 0, 1, 0, 0, 0, 1],
        "e9half" => [1, 0, 0, 0, 1, 0, 0, 0, 0.5],
        _ => [0, 1, 0, 1, 0, 0, 0, 0, 1],   // X and Y swapped
    };

    // ------------------------------------------------------------ 10.10 lut16 and 10.11 lut8

    /// <summary>
    /// ICC.1, 10.10 sentences 1–3, 25–42, 43–51, 87–101 and 10.11 sentences 1–3, 18–35, 68–83: with XYZ as the input of a lut16 or lut8 the
    /// 3 × 3 matrix (e1 to e9, row by row) is applied before the input curves — e1 = 0.5 halves X, e9 = 0.5 halves Z, [0 1 0 / 1 0 0 / 0 0 1]
    /// swaps X and Y; curves and table are the identity here.
    /// </summary>
    [Theory]
    [InlineData("lut16", "e1half")]
    [InlineData("lut16", "e9half")]
    [InlineData("lut16", "swapXY")]
    [InlineData("lut8", "e1half")]
    [InlineData("lut8", "e9half")]
    [InlineData("lut8", "swapXY")]
    public void Lut_XyzInput_MatrixApplied(string kind, string matrix)
    {
        var m = RIcc_MatrixCase(matrix);
        var lut = RIcc_Lut(kind, 3, 3, 2, i => [i[0] * 65535, i[1] * 65535, i[2] * 65535], m);
        var profile = IccProfile.Parse(RIcc_TableProfile("spac", RIcc_XyzSpace, RIcc_XyzSpace, lut))!;
        Assert.NotNull(profile);
        float[] input = [0.2f, 0.4f, 0.3f];
        var xyz = RIcc_ToXyz(profile, input);
        const float scale = 65535f / 32768f;
        for (var r = 0; r < 3; r++)
        {
            var expected = (float)(m[3 * r] * input[0] + m[3 * r + 1] * input[1] + m[3 * r + 2] * input[2]) * scale;
            Assert.InRange(xyz[r], expected - 0.005f, expected + 0.005f);
        }
    }

    /// <summary>
    /// ICC.1, 10.10 sentences 12–14, 22–24 (writer side): every lut16 table of the bundled CMYK profile has the reserved bytes 4–7 and the
    /// padding byte 11 at zero.
    /// </summary>
    [Fact]
    public void Fogra39_Lut16Tags_ReservedBytesAndPaddingZero()
    {
        var data = CmykProfile.Bytes();
        var tags = RIcc_FograMft2();
        Assert.NotEmpty(tags);
        foreach (var (sig, offset, _) in tags)
        {
            Assert.True(data.AsSpan(offset + 4, 4).IndexOfAnyExcept((byte)0) < 0, sig);
            Assert.Equal(0, data[offset + 11]);
        }
    }

    /// <summary>
    /// ICC.1, 10.10 sentences 12–14, 22–24 and 10.11 sentences 6–17, 45 (reader side): a CMYK table profile whose reserved bytes 4–7 are FFh
    /// and whose padding byte 11 is 1 is still read, with the same colours as the clean one.
    /// </summary>
    [Theory]
    [InlineData("lut16")]
    [InlineData("lut8")]
    public void Lut_ReservedBytesAndPaddingSet_ReadAlike(string kind)
    {
        // a simple CMYK formula: lightness falls with every ink
        var clean = RIcc_Lut(kind, 4, 3, 3, i => [Math.Max(0, 0xFF00 - (i[0] + i[1] + i[2] + 2 * i[3]) * 0x1800), 0x8000, 0x8000]);
        var dirty = (byte[])clean.Clone();
        dirty.AsSpan(4, 4).Fill(0xFF);
        dirty[11] = 1;
        RIcc_AssertSameColours(RIcc_TableProfile("prtr", IccProfile.CmykSpace, RIcc_LabSpace, clean),
            RIcc_TableProfile("prtr", IccProfile.CmykSpace, RIcc_LabSpace, dirty));
    }

    /// <summary>
    /// ICC.1, 10.10 sentences 15–21 and 10.11 sentences 6–17, 45: the input channels i must match the data colour space, the outputs o the PCS
    /// (three) and the grid points g be at least two — a CMYK profile with i = 3, with o = 4 or with g = 1 is unreadable; an ICCBased space
    /// with /Alternate /DeviceGray then gives DeviceGray, and the PDF/A check does not take it as an output profile.
    /// </summary>
    [Theory]
    [InlineData("lut16", "inputs3")]
    [InlineData("lut16", "outputs4")]
    [InlineData("lut16", "grid1")]
    [InlineData("lut8", "inputs3")]
    [InlineData("lut8", "outputs4")]
    [InlineData("lut8", "grid1")]
    public void Lut_ChannelsOrGridWrong_Unreadable_AlternateApplies(string kind, string wrong)
    {
        var (inputs, outputs, grid) = wrong switch { "inputs3" => (3, 3, 2), "outputs4" => (4, 4, 2), _ => (4, 3, 1) };
        var lut = RIcc_Lut(kind, inputs, outputs, grid, i => [.. Enumerable.Repeat(0x8000, outputs)]);
        var data = RIcc_TableProfile("prtr", IccProfile.CmykSpace, RIcc_LabSpace, lut);
        Assert.Null(IccProfile.Parse(data));
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        Assert.Same(ColorSpace.DeviceGray, RIcc_IccBased(doc, data, 4, "DeviceGray"));
        Assert.Null(PdfACheck.UsableProfile(data, 4));
    }

    /// <summary>
    /// ICC.1, 10.10 sentences 80–81 and 10.11 sentences 56–57: the values of a Lab data colour space go linearly onto 0…1 before the table — an
    /// ICCBased space with /Range [0 100 −128 127 −128 127] and the colour 50 0 0 meets the table at its middle, mid grey (the table here
    /// passes L through and keeps a* = b* = 0).
    /// </summary>
    [Theory]
    [InlineData("lut16")]
    [InlineData("lut8")]
    public void LabDataSpace_RangeMapsOntoTheTable(string kind)
    {
        var lut = RIcc_Lut(kind, 3, 3, 2, i => [i[0] == 1 ? 0xFF00 : 0, 0x8000, 0x8000]);
        var data = RIcc_TableProfile("spac", RIcc_LabSpace, RIcc_LabSpace, lut);
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var grey = RIcc_IccBased(doc, data, 3, range: [0, 100, -128, 127, -128, 127]).ToRgb([50, 0, 0]);
        // L* 50 is sRGB 119
        Assert.True(grey.R is > 112 and < 126 && Math.Abs(grey.R - grey.G) <= 3 && Math.Abs(grey.R - grey.B) <= 3, grey.ToString());
    }

    /// <summary>
    /// ICC.1, 10.10 sentences 82–83 and 136–142 (PCS XYZ): table entries are divided by 65 535; with PCS XYZ the entry 8000h is exactly 1.0
    /// (8000h / 65 535 · 65 535 / 32 768).
    /// </summary>
    [Fact]
    public void Lut16_PcsXyz_EntryHalfIsOne()
    {
        var lut = RIcc_Lut16(3, 3, 2, _ => [0x8000, 0x8000, 0x8000]);
        var profile = IccProfile.Parse(RIcc_TableProfile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, lut))!;
        foreach (var v in RIcc_ToXyz(profile, 0.3f, 0.6f, 0.9f)) Assert.InRange(v, 1 - 1e-5f, 1 + 1e-5f);
    }

    /// <summary>
    /// ICC.1, 10.10 sentence 102 and 10.11 sentences 68–83 (a): the matrix is the identity unless the input is XYZ — an RGB table whose matrix is
    /// not the identity keeps today's reading: the same colours as with the identity (the matrix is passed over).
    /// </summary>
    [Theory]
    [InlineData("lut16")]
    [InlineData("lut8")]
    public void Lut_RgbMatrixNotIdentity_ColoursAsWithIdentity(string kind)
    {
        Func<int[], int[]> clut = i => [i[0] * 0x4000, i[1] * 0x4000, i[2] * 0x4000];
        var identity = RIcc_Lut(kind, 3, 3, 3, clut);
        var other = RIcc_Lut(kind, 3, 3, 3, clut, [0.5, 0.2, 0, 0, 1, 0, 0.1, 0, 0.7]);
        RIcc_AssertSameColours(RIcc_TableProfile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, identity),
            RIcc_TableProfile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, other));
    }

    /// <summary>ICC.1, 10.10 sentence 102 (writer side): every lut16 table of the bundled CMYK profile (none has XYZ input) has the identity matrix.</summary>
    [Fact]
    public void Fogra39_Lut16Tags_IdentityMatrix()
    {
        var data = CmykProfile.Bytes();
        foreach (var (sig, offset, _) in RIcc_FograMft2())
            for (var k = 0; k < 9; k++)
                Assert.True(BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 12 + 4 * k)) == (k % 4 == 0 ? 65536 : 0), $"{sig} e{k + 1}");
    }

    /// <summary>
    /// ICC.1, 10.10 sentences 103–107: the input and output curves of a lut16 have 2 to 4 096 entries — 1 or 4 097 makes the profile unreadable.
    /// </summary>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(4097, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 4097)]
    public void Lut16_CurveEntriesOutsideTwoTo4096_Unreadable(int inEntries, int outEntries)
    {
        var lut = RIcc_Lut16(3, 3, 2, _ => [0x8000, 0x8000, 0x8000], inEntries: inEntries, outEntries: outEntries);
        Assert.Null(IccProfile.Parse(RIcc_TableProfile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, lut)));
        // the same with two entries each is read
        Assert.NotNull(IccProfile.Parse(RIcc_TableProfile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, RIcc_Lut16(3, 3, 2, _ => [0x8000, 0x8000, 0x8000]))));
    }

    /// <summary>
    /// ICC.1, 10.10 sentences 119–133, 135 and 10.11 sentences 103–105: the inputs follow the components of the data colour space in order — a
    /// 5CLR table (five inputs) is read and its fifth component acts; 9CLR exceeds the tables evaluated (at most eight inputs): unreadable, an
    /// ICCBased space with /Alternate /DeviceGray gives DeviceGray.
    /// </summary>
    [Theory]
    [InlineData("lut16")]
    [InlineData("lut8")]
    public void Lut_FiveColoursRead_NineColoursAlternate(string kind)
    {
        // only the fifth colour darkens
        var five = RIcc_TableProfile("prtr", RIcc_Sig("5CLR"), RIcc_LabSpace, RIcc_Lut(kind, 5, 3, 2, i => [i[4] == 1 ? 0 : 0xFF00, 0x8000, 0x8000]));
        var profile = IccProfile.Parse(five);
        Assert.NotNull(profile);
        Assert.Equal(5, profile!.Channels);
        Assert.True(RIcc_ToXyz(profile, 0, 0, 0, 0, 1)[1] < 0.01f);
        Assert.True(RIcc_ToXyz(profile, 1, 1, 1, 1, 0)[1] > 0.99f);
        var nine = RIcc_TableProfile("prtr", RIcc_Sig("9CLR"), RIcc_LabSpace, RIcc_Lut(kind, 9, 3, 2, _ => [0x8000, 0x8000, 0x8000]));
        Assert.Null(IccProfile.Parse(nine));
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        Assert.Same(ColorSpace.DeviceGray, RIcc_IccBased(doc, nine, 9, "DeviceGray"));
    }

    /// <summary>
    /// ICC.1, 10.10 sentences 136–142, 143–147, 152–153, 155: with PCS Lab a lut16 uses the legacy 16-bit encoding — L* FF00h is exactly 100
    /// (times 65 535 / 65 280), a* and b* 8000h are 0: the white of the PCS, D50 (the new encoding would give L* 99.6).
    /// </summary>
    [Fact]
    public void Lut16_LegacyLab_FF00IsLightness100()
    {
        var lut = RIcc_Lut16(3, 3, 2, _ => [0xFF00, 0x8000, 0x8000]);
        var xyz = RIcc_ToXyz(IccProfile.Parse(RIcc_TableProfile("mntr", IccProfile.RgbSpace, RIcc_LabSpace, lut))!, 0.5f, 0.5f, 0.5f);
        Assert.InRange(xyz[0], IccProfile.WhiteX - 1e-4f, IccProfile.WhiteX + 1e-4f);
        Assert.InRange(xyz[1], 1 - 1e-4f, 1 + 1e-4f);
        Assert.InRange(xyz[2], IccProfile.WhiteZ - 1e-4f, IccProfile.WhiteZ + 1e-4f);
    }

    /// <summary>
    /// ICC.1, 10.10 sentences 143–147 (writer side): the output curve for L* of each A2B table of the bundled CMYK profile stays at or below
    /// FF00h (L* 100) — no value above 100.
    /// </summary>
    [Fact]
    public void Fogra39_A2BLightnessCurve_AtMostFF00()
    {
        var data = CmykProfile.Bytes();
        var tables = RIcc_FograMft2().Where(t => t.Sig.StartsWith("A2B", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(tables);
        foreach (var (sig, offset, _) in tables)
        {
            int inputs = data[offset + 8], outputs = data[offset + 9], grid = data[offset + 10];
            int inEntries = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 48)), outEntries = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 50));
            var at = offset + 52 + 2 * inEntries * inputs + 2 * (int)Math.Pow(grid, inputs) * outputs;
            for (var j = 0; j < outEntries; j++) Assert.True(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 2 * j)) <= 0xFF00, $"{sig} {j}");
        }
    }

    /// <summary>
    /// ICC.1, 10.10 sentences 148–151: a* and b* in the legacy 16-bit encoding — 0000h is −128, 8000h is 0, FF00h is 127.
    /// </summary>
    [Theory]
    [InlineData(0x0000, -128)]
    [InlineData(0x8000, 0)]
    [InlineData(0xFF00, 127)]
    public void Lut16_LegacyLab_ABEncoding(int entry, double expected)
    {
        var lut = RIcc_Lut16(3, 3, 2, _ => [0xFF00, entry, entry]);
        var xyz = RIcc_ToXyz(IccProfile.Parse(RIcc_TableProfile("mntr", IccProfile.RgbSpace, RIcc_LabSpace, lut))!, 0.5f, 0.5f, 0.5f);
        var (l, a, b) = IccColor.XyzToLab(xyz[0], xyz[1], xyz[2]);
        Assert.InRange(l, 99.95f, 100.05f);
        Assert.InRange(a, expected - 0.05, expected + 0.05);
        Assert.InRange(b, expected - 0.05, expected + 0.05);
    }

    /// <summary>
    /// ICC.1, 10.10 sentence 154: values that only the legacy encoding holds are clipped per component when converted — L* FFFFh gives 100
    /// (Y 1.0), a* FFFFh gives 127.
    /// </summary>
    [Fact]
    public void Lut16_LegacyLabAboveFF00_Clipped()
    {
        var lut = RIcc_Lut16(3, 3, 2, _ => [0xFFFF, 0xFFFF, 0x8000]);
        var xyz = RIcc_ToXyz(IccProfile.Parse(RIcc_TableProfile("mntr", IccProfile.RgbSpace, RIcc_LabSpace, lut))!, 0.5f, 0.5f, 0.5f);
        var (l, a, _) = IccColor.XyzToLab(xyz[0], xyz[1], xyz[2]);
        Assert.InRange(xyz[1], 1 - 1e-4f, 1 + 1e-4f);
        Assert.InRange(l, 99.95f, 100.05f);
        Assert.InRange(a, 126.95f, 127.05f);
    }

    /// <summary>
    /// ICC.1, 10.11 sentences 47–49, 52–55 and 102: curves and table of a lut8 map 0…1 onto 0…1 with evenly spaced points; a table axis of two
    /// points is interpolated linearly — a grey lut8 (one input, linear curves, table L 00h and FFh) gives L* 25 at 0.25 and L* 50 at 0.5.
    /// </summary>
    [Theory]
    [InlineData(0.25f, 25)]
    [InlineData(0.5f, 50)]
    public void Lut8_GreyTable_LinearBetweenItsPoints(float grey, float lightness)
    {
        var lut = RIcc_Lut8(1, 3, 2, i => [i[0] == 1 ? 0xFF : 0, 0x80, 0x80]);
        var xyz = RIcc_ToXyz(IccProfile.Parse(RIcc_TableProfile("mntr", IccProfile.GraySpace, RIcc_LabSpace, lut))!, grey);
        var (l, _, _) = IccColor.XyzToLab(xyz[0], xyz[1], xyz[2]);
        Assert.InRange(l, lightness - 0.05f, lightness + 0.05f);
    }

    /// <summary>
    /// ICC.1, 10.11 sentences 58–59 and 61–65: lut8 entries are divided by 255; with PCS Lab the 8-bit Lab encoding applies — FFh 80h 80h is
    /// L* 100, a* = b* = 0: the white of the PCS, D50.
    /// </summary>
    [Fact]
    public void Lut8_PcsLab_FullEntryIsWhite()
    {
        var lut = RIcc_Lut8(3, 3, 2, _ => [0xFF, 0x80, 0x80]);
        var xyz = RIcc_ToXyz(IccProfile.Parse(RIcc_TableProfile("mntr", IccProfile.RgbSpace, RIcc_LabSpace, lut))!, 0.2f, 0.5f, 0.8f);
        Assert.InRange(xyz[0], IccProfile.WhiteX - 1e-4f, IccProfile.WhiteX + 1e-4f);
        Assert.InRange(xyz[1], 1 - 1e-4f, 1 + 1e-4f);
        Assert.InRange(xyz[2], IccProfile.WhiteZ - 1e-4f, IccProfile.WhiteZ + 1e-4f);
    }

    /// <summary>
    /// ICC.1, 10.11 sentences 66–67: 8-bit PCS XYZ is not defined, its reading is left to the implementation — today's reading as 16-bit PCS XYZ:
    /// the entry 80h is 128/255 · 65 535/32 768 ≈ 1.0039.
    /// </summary>
    [Fact]
    public void Lut8_PcsXyz_ReadAsSixteenBitXyz()
    {
        var lut = RIcc_Lut8(3, 3, 2, _ => [0x80, 0x80, 0x80]);
        var xyz = RIcc_ToXyz(IccProfile.Parse(RIcc_TableProfile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, lut))!, 0.5f, 0.5f, 0.5f);
        const float expected = 128f / 255 * 65535 / 32768;
        foreach (var v in xyz) Assert.InRange(v, expected - 1e-5f, expected + 1e-5f);
    }

    /// <summary>
    /// ICC.1, 10.11 sentences 89–94 and 103–105: the table of a lut8 runs with the first input slowest, the inputs in the order of the data colour
    /// space — a CMYK lut8 (g 2) dark only at the corner C = 1, M = Y = K = 0 gives (1, 0, 0, 0) dark and (0, 0, 0, 1) light.
    /// </summary>
    [Fact]
    public void Lut8_Cmyk_FirstInputSlowest()
    {
        var lut = RIcc_Lut8(4, 3, 2, i => [i[0] == 1 && i[1] + i[2] + i[3] == 0 ? 0 : 0xFF, 0x80, 0x80]);
        var profile = IccProfile.Parse(RIcc_TableProfile("prtr", IccProfile.CmykSpace, RIcc_LabSpace, lut))!;
        Assert.True(RIcc_ToXyz(profile, 1, 0, 0, 0)[1] < 0.01f);
        Assert.True(RIcc_ToXyz(profile, 0, 0, 0, 1)[1] > 0.99f);
        Assert.True(RIcc_ToXyz(profile, 0, 1, 0, 0)[1] > 0.99f);
    }
}
