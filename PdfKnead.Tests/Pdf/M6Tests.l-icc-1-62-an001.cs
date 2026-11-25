using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M6 draft l-icc-1-62-an001: ICC.1:2022 sections 1 to 6.2 and PDF Association AN001 (black point compensation). The profiles are built in
/// the tests. The helpers <c>IccL_*</c> here also serve the drafts m-icc-63-65, n-icc-7 and q-icc-10-109.
/// </summary>
public sealed partial class M6NormTests
{
    // ------------------------------------------------------------ profile building (shared with the packages m, n, q)

    private static uint IccL_Sig(string s) => BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(s));

    private static uint IccL_U32(byte[] d, int at) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at));

    private static void IccL_SetU32(byte[] d, int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(at), v);

    /// <summary>The white of the PCS (D50) as words of the 16-bit PCSXYZ encoding (1.0 = 8000h), scaled by <paramref name="k"/>.</summary>
    private static ushort[] IccL_D50Words(double k = 1) =>
        [(ushort)Math.Round(k * 0.9642 * 32768), (ushort)Math.Round(k * 32768), (ushort)Math.Round(k * 0.8249 * 32768)];

    /// <summary>
    /// A profile: header (size, version, class, data colour space, PCS, 'acsp', the D50 illuminant) and the tags in order, each at a multiple of
    /// four bytes; entries whose data are the same array share the bytes (one offset and size).
    /// </summary>
    private static byte[] IccL_Profile(string space, string pcs, (string Sig, byte[] Data)[] tags, byte version = 2, string deviceClass = "mntr")
    {
        var header = new byte[128];
        header[8] = version;
        Encoding.ASCII.GetBytes(deviceClass, header.AsSpan(12, 4));
        Encoding.ASCII.GetBytes(space, header.AsSpan(16, 4));
        Encoding.ASCII.GetBytes(pcs, header.AsSpan(20, 4));
        Encoding.ASCII.GetBytes("acsp", header.AsSpan(36, 4));
        IccL_Xyz(0.9642, 1, 0.8249).AsSpan(8, 12).CopyTo(header.AsSpan(68));
        return IccL_Assemble(header, tags);
    }

    /// <summary>A profile of a header (its size set here) and tags; tags with the same data array share the bytes.</summary>
    private static byte[] IccL_Assemble(byte[] header, (string Sig, byte[] Data)[] tags)
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
        header.AsSpan(0, 128).CopyTo(d);
        body.ToArray().CopyTo(d, table);
        IccL_SetU32(d, 0, (uint)d.Length);
        IccL_SetU32(d, 128, (uint)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            Encoding.ASCII.GetBytes(entries[i].Sig, d.AsSpan(132 + 12 * i, 4));
            IccL_SetU32(d, 136 + 12 * i, (uint)entries[i].Offset);
            IccL_SetU32(d, 140 + 12 * i, (uint)entries[i].Size);
        }
        return d;
    }

    /// <summary>The entries of the tag table (signature, offset, size).</summary>
    private static List<(string Sig, int Offset, int Size)> IccL_Entries(byte[] p)
    {
        var count = (int)IccL_U32(p, 128);
        var entries = new List<(string, int, int)>();
        for (var i = 0; i < count; i++)
            entries.Add((Encoding.ASCII.GetString(p, 132 + 12 * i, 4), (int)IccL_U32(p, 136 + 12 * i), (int)IccL_U32(p, 140 + 12 * i)));
        return entries;
    }

    /// <summary>The data of the first tag with this signature; null if there is none.</summary>
    private static byte[]? IccL_Tag(byte[] p, string sig) =>
        IccL_Entries(p).FirstOrDefault(e => e.Sig == sig) is { Sig: not null } e ? p.AsSpan(e.Offset, e.Size).ToArray() : null;

    /// <summary>The profile with its header and tags as they are and further tags appended (packed anew).</summary>
    private static byte[] IccL_WithTags(byte[] p, params (string Sig, byte[] Data)[] extra)
    {
        var own = IccL_Entries(p).Select(e => (e.Sig, p.AsSpan(e.Offset, e.Size).ToArray()));
        return IccL_Assemble(p.AsSpan(0, 128).ToArray(), [.. own, .. extra]);
    }

    private static byte[] IccL_Xyz(double x, double y, double z)
    {
        var d = new byte[20];
        Encoding.ASCII.GetBytes("XYZ ", d);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(8), (int)Math.Round(x * 65536));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(12), (int)Math.Round(y * 65536));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(16), (int)Math.Round(z * 65536));
        return d;
    }

    /// <summary>curveType with the given table (n = number of values; none: identity).</summary>
    private static byte[] IccL_Curv(params ushort[] values)
    {
        var d = new byte[12 + 2 * values.Length];
        Encoding.ASCII.GetBytes("curv", d);
        IccL_SetU32(d, 8, (uint)values.Length);
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(12 + 2 * i), values[i]);
        return d;
    }

    /// <summary>curveType with one entry: a gamma as u8Fixed8Number.</summary>
    private static byte[] IccL_Gamma(double gamma) => IccL_Curv((ushort)Math.Round(gamma * 256));

    /// <summary>
    /// lut16Type with a grid of two points per input, identity matrix and identity curves (two entries): <paramref name="corners"/> holds the three
    /// output words of each corner, the first input varying slowest.
    /// </summary>
    private static byte[] IccL_Lut16(int inputs, params ushort[][] corners)
    {
        var points = 1 << inputs;
        var d = new byte[52 + inputs * 4 + points * 6 + 3 * 4];
        Encoding.ASCII.GetBytes("mft2", d);
        d[8] = (byte)inputs;
        d[9] = 3;
        d[10] = 2;
        foreach (var at in (ReadOnlySpan<int>)[12, 28, 44]) IccL_SetU32(d, at, 0x10000);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(50), 2);
        var o = 52;
        for (var i = 0; i < inputs; i++, o += 4) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(o + 2), 65535);
        for (var c = 0; c < points; c++, o += 6)
            for (var k = 0; k < 3; k++) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(o + 2 * k), corners[c % corners.Length][k]);
        for (var i = 0; i < 3; i++, o += 4) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(o + 2), 65535);
        return d;
    }

    /// <summary>lut8Type with a grid of two points per input, identity matrix and identity tables; three output bytes per corner.</summary>
    private static byte[] IccL_Lut8(int inputs, params byte[][] corners)
    {
        var points = 1 << inputs;
        var d = new byte[48 + inputs * 256 + points * 3 + 3 * 256];
        Encoding.ASCII.GetBytes("mft1", d);
        d[8] = (byte)inputs;
        d[9] = 3;
        d[10] = 2;
        foreach (var at in (ReadOnlySpan<int>)[12, 28, 44]) IccL_SetU32(d, at, 0x10000);
        var o = 48;
        for (var i = 0; i < inputs; i++, o += 256)
            for (var v = 0; v < 256; v++) d[o + v] = (byte)v;
        for (var c = 0; c < points; c++, o += 3) corners[c % corners.Length].AsSpan(0, 3).CopyTo(d.AsSpan(o));
        for (var i = 0; i < 3; i++, o += 256)
            for (var v = 0; v < 256; v++) d[o + v] = (byte)v;
        return d;
    }

    /// <summary>
    /// lutAToBType: B curves (identity), a 16-bit CLUT with two points per input (three output words per corner, the first input varying
    /// slowest) and A curves (identity); no matrix, no M curves.
    /// </summary>
    private static byte[] IccL_LutAToB(int inputs, params ushort[][] corners)
    {
        var points = 1 << inputs;
        const int b = 32, clut = b + 3 * 12;
        var data = points * 6;
        var a = clut + 20 + ((data + 3) & ~3);
        var d = new byte[a + inputs * 12];
        Encoding.ASCII.GetBytes("mAB ", d);
        d[8] = (byte)inputs;
        d[9] = 3;
        IccL_SetU32(d, 12, b);
        IccL_SetU32(d, 24, clut);
        IccL_SetU32(d, 28, (uint)a);
        for (var i = 0; i < 3; i++) Encoding.ASCII.GetBytes("curv", d.AsSpan(b + 12 * i));
        for (var i = 0; i < inputs; i++) d[clut + i] = 2;
        d[clut + 16] = 2;
        for (var c = 0; c < points; c++)
            for (var k = 0; k < 3; k++) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(clut + 20 + 6 * c + 2 * k), corners[c % corners.Length][k]);
        for (var i = 0; i < inputs; i++) Encoding.ASCII.GetBytes("curv", d.AsSpan(a + 12 * i));
        return d;
    }

    // AdobeRGB primaries adapted to D50 (columns red, green, blue), a gamma of 2.2: not sRGB, so ICCBased does not pass it through as DeviceRGB
    private static (string, byte[])[] IccL_AdobeTags()
    {
        var curve = IccL_Gamma(2.2);
        return
        [
            ("rXYZ", IccL_Xyz(0.6097559, 0.3111242, 0.0194811)), ("gXYZ", IccL_Xyz(0.2052401, 0.6256560, 0.0608902)),
            ("bXYZ", IccL_Xyz(0.1492240, 0.0632197, 0.7448387)), ("rTRC", curve), ("gTRC", curve), ("bTRC", curve),
            ("wtpt", IccL_Xyz(0.9642, 1, 0.8249)),
        ];
    }

    /// <summary>An RGB matrix profile (AdobeRGB-like) with further tags.</summary>
    private static byte[] IccL_AdobeRgb(params (string Sig, byte[] Data)[] extra) => IccL_Profile("RGB ", "XYZ ", [.. IccL_AdobeTags(), .. extra]);

    /// <summary>A grey profile with PCS XYZ whose A2Bx tags are given.</summary>
    private static byte[] IccL_Grey(params (string Sig, byte[] Data)[] tags) => IccL_Profile("GRAY", "XYZ ", [.. tags, ("wtpt", IccL_Xyz(0.9642, 1, 0.8249))]);

    /// <summary>A grey lut16 whose output is the constant XYZ k · D50.</summary>
    private static byte[] IccL_ConstantGrey(double k) => IccL_Lut16(1, IccL_D50Words(k));

    private static float[] IccL_ToXyz(IccProfile p, params float[] device)
    {
        var xyz = new float[3];
        p.ToXyz(device, xyz);
        return xyz;
    }

    /// <summary>An ICCBased space of the profile (with /Alternate if given), built in a throwaway document.</summary>
    private static ColorSpace IccL_Space(byte[] profile, int n, string? alternate = null)
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var dict = TestPdf.Dict(("N", PdfInteger.Of(n)));
        if (alternate is not null) dict.Set(PdfName.Get("Alternate"), TestPdf.N(alternate));
        return ColorSpace.Create(new PdfArray([TestPdf.N("ICCBased"), new PdfStream(dict, profile)]), null, doc);
    }

    /// <summary>The simple CMYK formula as a baked profile (lut16, PCS Lab, legacy encoding).</summary>
    private static readonly Lazy<byte[]> IccL_NaiveCmyk = new(() => CmykProfile.Bake((c, m, y, k) => ((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k))));

    /// <summary>A CMYK profile whose black is only a 20 % grey (sRGB 0.2): black point compensation pulls it down to black.</summary>
    private static readonly Lazy<byte[]> IccL_LightBlack = new(() =>
        CmykProfile.Bake((c, m, y, k) => (0.2 + 0.8 * (1 - c) * (1 - k), 0.2 + 0.8 * (1 - m) * (1 - k), 0.2 + 0.8 * (1 - y) * (1 - k))));

    /// <summary>The profiles PDF-Knead writes itself.</summary>
    private static IEnumerable<(string Name, byte[] Data)> IccL_OwnProfiles() =>
    [
        ("Srgb", CalibratedProfile.Srgb()), ("SrgbGray", CalibratedProfile.SrgbGray()), ("RommRgb", CalibratedProfile.RommRgb()),
        ("Primaries", CalibratedProfile.Primaries((0.64, 0.33), (0.30, 0.60), (0.15, 0.06), (0.3127, 0.3290), "BT.709")),
        ("CalGray", CalibratedProfile.Gray(null)), ("CalRGB", CalibratedProfile.Rgb(null)),
    ];

    private static SkiaSharp.SKColor IccL_Pixel(SkiaSharp.SKBitmap b, int x, int y) => b.GetPixel(x, b.Height - 1 - y);

    /// <summary>A page of 200 × 200 with the content and resources given, rendered at 72 dpi.</summary>
    private static SkiaSharp.SKBitmap IccL_Render(string content, Func<TestPdf, PdfDictionary> resources)
    {
        var (doc, list) = InterpreterTests.Page(content, resources);
        using (doc) return SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
    }

    // ------------------------------------------------------------ tests

    /// <summary>
    /// ICC.1 3.1.2 sentence 1: the texts PDF-Knead writes into profiles (the ASCII part of the description, the copyright text) are bytes of
    /// ISO/IEC 646 — all below 80h — and end with a NUL byte; for the own profiles and the version-2 copy of a version-4 profile.
    /// </summary>
    [Fact]
    public void IccOwnProfiles_DescAndCprt_AsciiStringsEndingInNull()
    {
        var profiles = IccL_OwnProfiles().Append(("AsVersion2", CalibratedProfile.AsVersion2(PdfARuleCases.Version4Rgb())!));
        foreach (var (name, p) in profiles)
        {
            var desc = IccL_Tag(p, "desc")!;
            Assert.Equal("desc", Encoding.ASCII.GetString(desc, 0, 4));
            var count = (int)IccL_U32(desc, 8);
            Assert.True(count >= 1 && 12 + count <= desc.Length, name);
            Assert.All(desc.AsSpan(12, count).ToArray(), b => Assert.True(b < 0x80, name));
            Assert.Equal(0, desc[12 + count - 1]);
            var cprt = IccL_Tag(p, "cprt")!;
            Assert.Equal("text", Encoding.ASCII.GetString(cprt, 0, 4));
            Assert.All(cprt.AsSpan(8).ToArray(), b => Assert.True(b < 0x80, name));
            Assert.Equal(0, cprt[^1]);
        }
    }

    /// <summary>
    /// ICC.1 3.1.23 sentence 1: PCSLAB in the 16-bit encoding of lutAToBType (L* 100 = FFFFh, a* = b* = 0 at 8080h): a grey profile whose table runs
    /// from L* 0 to L* 100 gives L* 0, 50 and 100 for the inputs 0, 0.5 and 1.
    /// </summary>
    [Fact]
    public void IccLutAToB_PcsLab16_LightnessZeroHalfFull()
    {
        var profile = IccProfile.Parse(IccL_Profile("GRAY", "Lab ", [("A2B0", IccL_LutAToB(1, [0, 0x8080, 0x8080], [0xFFFF, 0x8080, 0x8080]))], version: 4))!;
        foreach (var (input, lightness) in new[] { (0f, 0f), (0.5f, 50f), (1f, 100f) })
        {
            var xyz = IccL_ToXyz(profile, input);
            var (l, a, b) = IccColor.XyzToLab(xyz[0], xyz[1], xyz[2]);
            Assert.InRange(l, lightness - 0.01f, lightness + 0.01f);
            Assert.InRange(a, -0.01f, 0.01f);
            Assert.InRange(b, -0.01f, 0.01f);
        }
    }

    /// <summary>
    /// ICC.1 4.3 sentences 1–4, 5 sentences 1–2 (also 6.3.4.1 sentences 3–4, 6.3.4.2 sentence 4, 6.4 sentences 4–5, 6.5 sentence 3): float32 values
    /// occur only in tags PDF-Knead does not read (D2Bx as multiProcessElementsType). A version-4 profile with D2B0 only is unreadable and the
    /// ICCBased space takes its /Alternate; next to an A2B1 or A2B0 table that table gives the colours.
    /// </summary>
    [Fact]
    public void IccD2B0Only_Unreadable_A2BTablesBesideItAreUsed()
    {
        // multiProcessElementsType: three inputs and outputs, no elements
        byte[] mpet = [.. "mpet"u8, 0, 0, 0, 0, 0, 3, 0, 3, 0, 0, 0, 0];
        var only = IccL_Profile("RGB ", "XYZ ", [("D2B0", mpet), ("wtpt", IccL_Xyz(0.9642, 1, 0.8249))], version: 4);
        Assert.Null(IccProfile.Parse(only));
        Assert.Same(ColorSpace.DeviceGray, IccL_Space(only, 3, "DeviceGray"));
        var half = IccL_D50Words(0.5);
        foreach (var tag in new[] { "A2B1", "A2B0" })
        {
            var both = IccL_Profile("RGB ", "XYZ ", [("D2B0", mpet), (tag, IccL_Lut16(3, half)), ("wtpt", IccL_Xyz(0.9642, 1, 0.8249))], version: 4);
            var xyz = IccL_ToXyz(IccProfile.Parse(both)!, 0.3f, 0.6f, 0.9f);
            Assert.InRange(xyz[1], 0.4999f, 0.5001f);
        }
    }

    /// <summary>
    /// ICC.1 4.5 sentences 1–14: response16Number is only part of responseCurveSet16Type, which PDF-Knead does not read: a matrix profile with an
    /// additional resp tag whose reserved bytes are not zero is read with the same colours as without it.
    /// </summary>
    [Fact]
    public void IccResponseCurveTag_IgnoredColoursUnchanged()
    {
        var resp = new byte[92];
        Encoding.ASCII.GetBytes("rspc", resp);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(8), 3);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(10), 1);
        IccL_SetU32(resp, 12, 16);
        Encoding.ASCII.GetBytes("StaA", resp.AsSpan(16));
        for (var c = 0; c < 3; c++) IccL_SetU32(resp, 20 + 4 * c, 1);
        for (var c = 0; c < 3; c++)
        {
            var at = 68 + 8 * c;
            BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(at), 0xFFFF);
            BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(at + 2), 0xABCD);   // reserved, not zero
            IccL_SetU32(resp, at + 4, 0x10000);
        }
        var with = IccProfile.Parse(IccL_AdobeRgb(("resp", resp)))!;
        var without = IccProfile.Parse(IccL_AdobeRgb())!;
        foreach (var v in new[] { 0.1f, 0.5f, 0.9f })
            Assert.Equal(IccL_ToXyz(without, v, 1 - v, v / 2), IccL_ToXyz(with, v, 1 - v, v / 2));
    }

    /// <summary>
    /// ICC.1 4.6 sentences 1–5: s15Fixed16Number covers −32768 to just below 32768. A CalRGB /Matrix with 40000 or −40000 for the X of red is
    /// written as the largest or smallest value of the type (7FFFFFFFh, 80000000h), not as a wrapped number.
    /// </summary>
    [Theory]
    [InlineData(40000, 0x7FFFFFFFu)]
    [InlineData(-40000, 0x80000000u)]
    public void IccCalRgbProfile_ColorantOutOfS15Fixed16Range_Clamped(double x, uint expected)
    {
        var profile = CalibratedProfile.Rgb(TestPdf.Dict(("Matrix", TestPdf.Box(x, 0, 0, 0, 1, 0, 0, 0, 1))));
        Assert.Equal(expected, IccL_U32(IccL_Tag(profile, "rXYZ")!, 8));
    }

    /// <summary>
    /// ICC.1 4.15 sentence 1 and 10.2 sentence 9: alphanumeric values are 7-bit ASCII. The version-2 copy of a version-4 profile whose description
    /// holds an en dash and an umlaut writes them as "?": the ASCII part of desc has only bytes below 80h and ends with 00h.
    /// </summary>
    [Fact]
    public void IccAsVersion2_NonAsciiDescription_SevenBitWithQuestionMarks()
    {
        var v2 = CalibratedProfile.AsVersion2(PdfARuleCases.Version4Rgb())!;
        var desc = IccL_Tag(v2, "desc")!;
        var count = (int)IccL_U32(desc, 8);
        Assert.Equal(Encoding.ASCII.GetBytes("Beispiel-RGB v4 ? Gr?n\0"), desc.AsSpan(12, count).ToArray());
    }

    /// <summary>
    /// ICC.1 5 sentences 4–5 (also 7.3.1 sentences 12–13, 10.1 sentence 4): the own profiles, the version-2 copy and the baked test profile use only
    /// registered tag signatures and the type signatures of chapter 10 (and desc, text of version 2); preferred CMM, maker and model stay 0.
    /// </summary>
    [Fact]
    public void IccOwnProfiles_OnlyRegisteredTagAndTypeSignatures()
    {
        HashSet<string> tags = ["desc", "wtpt", "cprt", "rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC", "kTRC", "chad", "A2B0"];
        HashSet<string> types = ["desc", "text", "XYZ ", "curv", "sf32", "mft2"];
        var profiles = IccL_OwnProfiles().Append(("AsVersion2", CalibratedProfile.AsVersion2(PdfARuleCases.Version4Rgb())!))
            .Append(("Bake", IccL_NaiveCmyk.Value));
        foreach (var (name, p) in profiles)
        {
            foreach (var (sig, offset, _) in IccL_Entries(p))
            {
                Assert.True(tags.Contains(sig), $"{name}: {sig}");
                Assert.True(types.Contains(Encoding.ASCII.GetString(p, offset, 4)), $"{name}: {sig} {Encoding.ASCII.GetString(p, offset, 4)}");
            }
            if (name is "Bake" or "AsVersion2") continue;
            Assert.Equal(0u, IccL_U32(p, 4));
            Assert.Equal(0u, IccL_U32(p, 48));
            Assert.Equal(0u, IccL_U32(p, 52));
        }
    }

    /// <summary>
    /// ICC.1 6.1 sentences 9–10 and 6.2.5 sentence 1: a profile carries one table per rendering intent. With A2B0, A2B1 and an own A2B2 the
    /// saturation intent takes A2B2, perceptual A2B0, relative colorimetric A2B1 — also through the ICCBased space.
    /// </summary>
    [Fact]
    public void IccSaturationIntent_UsesItsOwnA2B2()
    {
        var data = IccL_Grey(("A2B0", IccL_ConstantGrey(0.2)), ("A2B1", IccL_ConstantGrey(0.4)), ("A2B2", IccL_ConstantGrey(0.6)));
        var profile = IccProfile.Parse(data)!;
        Assert.InRange(IccL_ToXyz(profile.ForIntent(2), 0.5f)[1], 0.5999f, 0.6001f);
        Assert.InRange(IccL_ToXyz(profile.ForIntent(0), 0.5f)[1], 0.1999f, 0.2001f);
        Assert.InRange(IccL_ToXyz(profile, 0.5f)[1], 0.3999f, 0.4001f);
        var space = IccL_Space(data, 1);
        // neutral greys of Y 0.6, 0.4 and 0.2: sRGB 203, 170, 124
        Assert.InRange(space.WithRendering(ColorSpace.Saturation, ColorSpace.BpcDefault).ToRgb([0.5]).R, 202, 204);
        Assert.InRange(space.ToRgb([0.5]).R, 169, 171);
        Assert.InRange(space.WithRendering(ColorSpace.Perceptual, ColorSpace.BpcDefault).ToRgb([0.5]).R, 123, 125);
    }

    private static PdfDictionary IccL_SrgbCalRgb() => TestPdf.Dict(("WhitePoint", TestPdf.Box(0.9505, 1, 1.089)),
        ("Matrix", TestPdf.Box(0.4124, 0.2126, 0.0193, 0.3576, 0.7152, 0.1192, 0.1805, 0.0722, 0.9505)));

    /// <summary>
    /// ICC.1 6.2.1 sentences 1–2: colorimetric values are adapted to the white of the PCS. The profile of a CalRGB space under D65 has colorants
    /// adapted to D50 — their sum is D50 within 0.001 — as a profile of version 2.1 without a chad tag.
    /// </summary>
    [Fact]
    public void IccCalRgbProfile_D65White_ColorantsSumToD50WithoutChad()
    {
        var p = CalibratedProfile.Rgb(IccL_SrgbCalRgb());
        Assert.Equal(0x02100000u, IccL_U32(p, 8));
        Assert.Null(IccL_Tag(p, "chad"));
        double[] sum = [0, 0, 0];
        foreach (var sig in new[] { "rXYZ", "gXYZ", "bXYZ" })
            for (var i = 0; i < 3; i++) sum[i] += BinaryPrimitives.ReadInt32BigEndian(IccL_Tag(p, sig).AsSpan(8 + 4 * i)) / 65536.0;
        Assert.InRange(sum[0], 0.9632, 0.9652);
        Assert.InRange(sum[1], 0.999, 1.001);
        Assert.InRange(sum[2], 0.8239, 0.8259);
    }

    /// <summary>
    /// ICC.1 6.2.1 sentences 13–14: the colorimetric intent image state tag is not read — a reasoned deviation (docs/normabgleich/sonderregeln.md
    /// S-B B57: rare in embedded profiles, a colour rendering of scenes is a function of its own): a matrix profile with ciis = scoe (scene
    /// colorimetry) converts exactly as without the tag.
    /// </summary>
    [Fact]
    public void IccSceneReferredTag_NoColourRendering()
    {
        byte[] ciis = [.. "sig "u8, 0, 0, 0, 0, .. "scoe"u8];
        var with = new IccTransform(IccProfile.Parse(IccL_AdobeRgb(("ciis", ciis)))!);
        var without = new IccTransform(IccProfile.Parse(IccL_AdobeRgb())!);
        float[] a = new float[3], b = new float[3];
        foreach (var v in new[] { 0f, 0.3f, 0.7f, 1f })
        {
            with.ToSrgb([v, v / 2, 1 - v], a);
            without.ToSrgb([v, v / 2, 1 - v], b);
            Assert.Equal(b, a);
        }
    }

    /// <summary>
    /// ICC.1 6.2.2 sentences 1–2: media-relative colorimetry puts the media white on the white of the PCS. The profile of a CalRGB space under
    /// D65 maps device white (1, 1, 1) to D50 within 0.001.
    /// </summary>
    [Fact]
    public void IccCalRgbProfile_D65White_DeviceWhiteGivesD50()
    {
        var xyz = IccL_ToXyz(IccProfile.Parse(CalibratedProfile.Rgb(IccL_SrgbCalRgb()))!, 1, 1, 1);
        Assert.InRange(xyz[0], 0.9632f, 0.9652f);
        Assert.InRange(xyz[1], 0.999f, 1.001f);
        Assert.InRange(xyz[2], 0.8239f, 0.8259f);
    }

    /// <summary>
    /// ICC.1 6.2.3 sentences 1–7: ICC-absolute colorimetry from the media-relative table, scaled with the media white (docs/FEHLERGEDAECHTNIS.md,
    /// No. 1129). A CMYK profile whose media white point is 0.9 · D50 (tinted paper), drawn with /AbsoluteColorimetric ri: the paper „0 0 0 0“
    /// is XYZ 0.9 · D50, a neutral sRGB grey of 243 (linear 0.9) — the paper tone shows; without the intent (relative colorimetric) it is white.
    /// </summary>
    [Fact]
    public void IccAbsoluteColorimetric_TintedPaperShowsItsTone()
    {
        var tinted = (byte[])IccL_NaiveCmyk.Value.Clone();
        Assert.Equal("wtpt", Encoding.ASCII.GetString(tinted, 132, 4));
        var wtpt = (int)IccL_U32(tinted, 136);
        IccL_Xyz(0.9 * 0.9642, 0.9, 0.9 * 0.8249).CopyTo(tinted, wtpt);
        using var b = IccL_Render("/AbsoluteColorimetric ri /CS0 cs 0 0 0 0 sc 0 0 100 200 re f /RelativeColorimetric ri 100 0 100 200 re f", pdf =>
            TestPdf.Dict(("ColorSpace", TestPdf.Dict(
            ("CS0", new PdfArray([TestPdf.N("ICCBased"), TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), tinted)))]))))));
        var c = IccL_Pixel(b, 50, 100);
        Assert.True(c is { Red: >= 242 and <= 244, Green: >= 242 and <= 244, Blue: >= 242 and <= 244 }, c.ToString());
        var relative = IccL_Pixel(b, 150, 100);
        Assert.True(relative is { Red: >= 254, Green: >= 254, Blue: >= 254 }, relative.ToString());
    }

    /// <summary>
    /// ICC.1 6.2.3 sentences 8–9: for display profiles the media white point is the white of the PCS: wtpt of the own sRGB profile and of a CalRGB
    /// profile under D65 is D50 as s15Fixed16 (0000F6D6h, 00010000h, 0000D32Dh).
    /// </summary>
    [Fact]
    public void IccOwnProfiles_MediaWhitePointIsD50()
    {
        foreach (var p in new[] { CalibratedProfile.Srgb(), CalibratedProfile.Rgb(IccL_SrgbCalRgb()) })
        {
            var wtpt = IccL_Tag(p, "wtpt")!;
            Assert.Equal(0x0000F6D6u, IccL_U32(wtpt, 8));
            Assert.Equal(0x00010000u, IccL_U32(wtpt, 12));
            Assert.Equal(0x0000D32Du, IccL_U32(wtpt, 16));
        }
    }

    /// <summary>
    /// AN001 General sentence 5: black point compensation as ISO 18619 scales it — for a neutral source black Ys and a destination black of 0,
    /// Y′ = (Y − Ys) / (1 − Ys). The profile with a black of sRGB 0.2 gives for "0 0 0 0.5" (sRGB 0.6 in the profile) that value, neutral.
    /// </summary>
    [Fact]
    public void IccBlackPointCompensation_NeutralBlack_ScaledAsIso18619()
    {
        static double Decode(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        static double Encode(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        double ys = Decode(0.2), y = Decode(0.6);
        var expected = Encode((y - ys) / (1 - ys));
        var transform = new IccTransform(IccProfile.Parse(IccL_LightBlack.Value)!);
        var rgb = new float[3];
        transform.ToSrgb([0, 0, 0, 0.5f], rgb);
        foreach (var c in rgb) Assert.InRange(c, expected - 0.003, expected + 0.003);
    }

    /// <summary>
    /// AN001 General sentence 14: /UseBlackPtComp has the states ON, OFF and Default, written exactly so. ON, Default and the misspelt /on
    /// compensate (black of the 20 % profile drawn below 12), OFF does not (40–62), a following /Default compensates again.
    /// </summary>
    [Fact]
    public void IccUseBlackPtComp_OnlyExactOffSwitchesItOff()
    {
        using var b = IccL_Render("/CS0 cs 0 0 0 1 sc /On gs 0 0 40 200 re f /Def gs 40 0 40 200 re f /Low gs 80 0 40 200 re f " +
                                  "/Off gs 120 0 40 200 re f /Def gs 160 0 40 200 re f", pdf => TestPdf.Dict(
            ("ColorSpace", TestPdf.Dict(("CS0", new PdfArray([TestPdf.N("ICCBased"),
                TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), IccL_LightBlack.Value)))])))),
            ("ExtGState", TestPdf.Dict(("On", TestPdf.Dict(("UseBlackPtComp", TestPdf.N("ON")))), ("Def", TestPdf.Dict(("UseBlackPtComp", TestPdf.N("Default")))),
                ("Low", TestPdf.Dict(("UseBlackPtComp", TestPdf.N("on")))), ("Off", TestPdf.Dict(("UseBlackPtComp", TestPdf.N("OFF"))))))));
        foreach (var x in new[] { 20, 60, 100, 180 }) Assert.True(IccL_Pixel(b, x, 100).Red < 12, $"{x}: {IccL_Pixel(b, x, 100)}");
        Assert.InRange(IccL_Pixel(b, 140, 100).Red, (byte)40, (byte)62);
    }

    /// <summary>
    /// AN001 General sentences 18–20, 24–26: switched off where colours must stay exact (a brand colour as a spot colour): a Separation whose
    /// alternate space is the ICC CMYK profile with the 20 % black, tint 1, is compensated by default and not under /UseBlackPtComp /OFF.
    /// </summary>
    [Fact]
    public void IccUseBlackPtCompOff_SeparationWithIccAlternate_NotCompensated()
    {
        using var b = IccL_Render("/Sep cs 1 sc 0 0 100 200 re f /Off gs 1 sc 100 0 100 200 re f", pdf =>
        {
            var icc = new PdfArray([TestPdf.N("ICCBased"), TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), IccL_LightBlack.Value)))]);
            var tint = TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)), ("C0", TestPdf.Box(0, 0, 0, 0)),
                ("C1", TestPdf.Box(0, 0, 0, 1)), ("N", PdfInteger.Of(1)));
            return TestPdf.Dict(("ColorSpace", TestPdf.Dict(("Sep", new PdfArray([TestPdf.N("Separation"), TestPdf.N("Hausfarbe"), icc, tint])))),
                ("ExtGState", TestPdf.Dict(("Off", TestPdf.Dict(("UseBlackPtComp", TestPdf.N("OFF")))))));
        });
        Assert.True(IccL_Pixel(b, 50, 100).Red < 12, IccL_Pixel(b, 50, 100).ToString());
        Assert.InRange(IccL_Pixel(b, 150, 100).Red, (byte)40, (byte)62);
    }
}
