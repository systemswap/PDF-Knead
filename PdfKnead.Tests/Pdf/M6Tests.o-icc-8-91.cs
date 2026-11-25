using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M6 draft o-icc-8-91 (ICC.1:2022, 8: tags required per profile class and their precedence; 9.1: tag definitions in general). The profiles
/// are built here; the builders (prefix IccO_) serve the drafts p-icc-92 and s-icc-1012-1013 as well.
/// </summary>
public sealed partial class M6NormTests
{
    // ------------------------------------------------------------ profile builders (shared by the files o, p and s)

    private const uint IccO_PcsXyz = 0x58595A20, IccO_PcsLab = 0x4C616220;

    /// <summary>u1Fixed15 of the XYZ encoding of tables: a table value v (0…1) is XYZ v · 65535 / 32768.</summary>
    private const double IccO_XyzScale = 65535.0 / 32768.0;

    /// <summary>
    /// A profile: header (version 2.1 unless given, device class, colour space, PCS, 'acsp', D50 illuminant) and the tags in order; entries
    /// whose data are the same array share one offset.
    /// </summary>
    private static byte[] IccO_Profile(string deviceClass, uint space, uint pcs, (string Sig, byte[] Data)[] tags, uint version = 0x02100000)
    {
        var header = new byte[128];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), version);
        Encoding.ASCII.GetBytes(deviceClass, header.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), space);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), pcs);
        "acsp"u8.CopyTo(header.AsSpan(36));
        IccO_XyzTag(0.9642, 1, 0.8249).AsSpan(8, 12).CopyTo(header.AsSpan(68));
        return IccO_Assemble(header, tags);
    }

    /// <summary>A profile of a header (size set here) and tags, each at a multiple of four bytes; the same array is placed once.</summary>
    private static byte[] IccO_Assemble(byte[] header, (string Sig, byte[] Data)[] tags)
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
        BinaryPrimitives.WriteUInt32BigEndian(d, (uint)d.Length);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(128), (uint)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            Encoding.ASCII.GetBytes(entries[i].Sig, d.AsSpan(132 + 12 * i, 4));
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(136 + 12 * i), (uint)entries[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(140 + 12 * i), (uint)entries[i].Size);
        }
        return d;
    }

    /// <summary>The entries of the tag table (signature, offset, size).</summary>
    private static List<(string Sig, int Offset, int Size)> IccO_Tags(byte[] profile)
    {
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(128));
        var tags = new List<(string, int, int)>();
        for (var i = 0; i < count; i++)
            tags.Add((Encoding.ASCII.GetString(profile, 132 + 12 * i, 4), (int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(136 + 12 * i)),
                (int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(140 + 12 * i))));
        return tags;
    }

    /// <summary>The data of a tag; null if the profile has none of that signature.</summary>
    private static byte[]? IccO_TagData(byte[] profile, string sig) =>
        IccO_Tags(profile).FirstOrDefault(t => t.Sig == sig) is { Sig: not null } t ? profile.AsSpan(t.Offset, t.Size).ToArray() : null;

    /// <summary>The profile packed anew with further tags (appended) and without the tags <paramref name="remove"/>; the header stays.</summary>
    private static byte[] IccO_With(byte[] profile, (string Sig, byte[] Data)[] add, params string[] remove)
    {
        var shared = new Dictionary<(int, int), byte[]>();
        var tags = new List<(string, byte[])>();
        foreach (var (sig, offset, size) in IccO_Tags(profile))
        {
            if (remove.Contains(sig)) continue;
            if (!shared.TryGetValue((offset, size), out var data)) shared[(offset, size)] = data = profile.AsSpan(offset, size).ToArray();
            tags.Add((sig, data));
        }
        tags.AddRange(add);
        return IccO_Assemble(profile[..128], [.. tags]);
    }

    /// <summary>XYZType.</summary>
    private static byte[] IccO_XyzTag(double x, double y, double z)
    {
        var d = new byte[20];
        "XYZ "u8.CopyTo(d);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(8), (int)Math.Round(x * 65536));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(12), (int)Math.Round(y * 65536));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(16), (int)Math.Round(z * 65536));
        return d;
    }

    /// <summary>curveType with the given table entries (0…1); none = identity.</summary>
    private static byte[] IccO_Curv(params double[] entries)
    {
        var d = new byte[12 + 2 * entries.Length];
        "curv"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), (uint)entries.Length);
        for (var i = 0; i < entries.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(12 + 2 * i), (ushort)Math.Round(entries[i] * 65535));
        return d;
    }

    /// <summary>curveType with one entry: a pure gamma (u8Fixed8Number).</summary>
    private static byte[] IccO_Gamma(double gamma)
    {
        var d = new byte[14];
        "curv"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(12), (ushort)Math.Round(gamma * 256));
        return d;
    }

    /// <summary>textType.</summary>
    private static byte[] IccO_Text(string text) => [.. "text"u8, 0, 0, 0, 0, .. Encoding.ASCII.GetBytes(text), 0];

    /// <summary>A tag of the given type signature with 40 bytes of meaningless content behind the reserved field.</summary>
    private static byte[] IccO_Junk(string type, int length = 48)
    {
        var d = new byte[length];
        Encoding.ASCII.GetBytes(type, d.AsSpan(0, 4));
        for (var i = 8; i < length; i++) d[i] = (byte)(i * 37 + 11);
        return d;
    }

    /// <summary>
    /// lut16Type with identity curves (two entries) and a table of <paramref name="grid"/> points per input; <paramref name="table"/> gives the
    /// raw output values 0…1 for the input values at a grid point (first input varying slowest).
    /// </summary>
    private static byte[] IccO_Lut16(int inputs, int outputs, int grid, Func<double[], double[]> table)
    {
        var points = (int)Math.Pow(grid, inputs);
        var d = new byte[52 + inputs * 4 + points * outputs * 2 + outputs * 4];
        "mft2"u8.CopyTo(d);
        d[8] = (byte)inputs;
        d[9] = (byte)outputs;
        d[10] = (byte)grid;
        foreach (var at in new[] { 12, 28, 44 }) BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(at), 65536);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(50), 2);
        var pos = 52;
        for (var i = 0; i < inputs; i++, pos += 4) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(pos + 2), 65535);
        for (var p = 0; p < points; p++)
        {
            var x = new double[inputs];
            for (int i = inputs - 1, rest = p; i >= 0; i--, rest /= grid) x[i] = rest % grid / (grid - 1.0);
            var v = table(x);
            for (var o = 0; o < outputs; o++, pos += 2) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(pos), (ushort)Math.Round(Math.Clamp(v[o], 0, 1) * 65535));
        }
        for (var o = 0; o < outputs; o++, pos += 4) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(pos + 2), 65535);
        return d;
    }

    /// <summary>A lut16 to PCS XYZ with three outputs and two points per input (exact for multilinear formulas).</summary>
    private static byte[] IccO_XyzTable(int inputs, Func<double[], (double X, double Y, double Z)> xyz) =>
        IccO_Lut16(inputs, 3, 2, v =>
        {
            var (x, y, z) = xyz(v);
            return [x / IccO_XyzScale, y / IccO_XyzScale, z / IccO_XyzScale];
        });

    /// <summary>The raw values of a lut16 to PCS Lab (legacy encoding, L 100 = 0xFF00, a and b 0 = 0x8000).</summary>
    private static double[] IccO_LabRaw(double l, double a, double b) => [l / 100 * 65280 / 65535, (a + 128) * 256 / 65535, (b + 128) * 256 / 65535];

    // three different multilinear CMYK formulas into XYZ
    private static (double, double, double) IccO_Plain(double[] v) => (0.9 * (1 - v[0]) * (1 - v[3]), 0.95 * (1 - v[1]) * (1 - v[3]), 0.8 * (1 - v[2]) * (1 - v[3]));
    private static (double, double, double) IccO_Swapped(double[] v) => (0.9 * (1 - v[1]) * (1 - v[3]), 0.95 * (1 - v[0]) * (1 - v[3]), 0.8 * (1 - v[2]) * (1 - v[3]));
    private static (double, double, double) IccO_Third(double[] v) => (0.3 * (1 - v[2]), 0.5 * (1 - v[0]) * (1 - v[1]), 0.6 * (1 - v[3]));

    /// <summary>A CMYK output profile (PCS XYZ) with white point and the given tables.</summary>
    private static byte[] IccO_Cmyk(string deviceClass = "prtr", params (string Sig, Func<double[], (double, double, double)> F)[] tables) =>
        IccO_Profile(deviceClass, IccProfile.CmykSpace, IccO_PcsXyz,
            [("wtpt", IccO_XyzTag(0.9642, 1, 0.8249)), .. tables.Select(t => (t.Sig, IccO_XyzTable(4, t.F)))]);

    // AdobeRGB colorants (D50) and gamma 2.2: a matrix profile that is not sRGB
    private static readonly double[] IccO_Adobe = [0.6097559, 0.2052401, 0.1492240, 0.3111242, 0.6256560, 0.0632197, 0.0194811, 0.0608902, 0.7448387];

    /// <summary>The six tags of a matrix profile (AdobeRGB colorants, gamma 2.2), the curve data shared.</summary>
    private static (string, byte[])[] IccO_MatrixTags()
    {
        var curve = IccO_Gamma(2.2);
        return
        [
            ("rXYZ", IccO_XyzTag(IccO_Adobe[0], IccO_Adobe[3], IccO_Adobe[6])), ("gXYZ", IccO_XyzTag(IccO_Adobe[1], IccO_Adobe[4], IccO_Adobe[7])),
            ("bXYZ", IccO_XyzTag(IccO_Adobe[2], IccO_Adobe[5], IccO_Adobe[8])), ("rTRC", curve), ("gTRC", curve), ("bTRC", curve),
        ];
    }

    /// <summary>A display matrix profile (AdobeRGB, gamma 2.2) with desc, cprt and wtpt.</summary>
    private static byte[] IccO_Matrix(string deviceClass = "mntr") =>
        IccO_Profile(deviceClass, IccProfile.RgbSpace, IccO_PcsXyz,
            [("desc", IccO_Text("Matrix")), ("cprt", IccO_Text("frei")), ("wtpt", IccO_XyzTag(0.9642, 1, 0.8249)), .. IccO_MatrixTags()]);

    /// <summary>XYZ of device values through the profile's tables of an intent.</summary>
    private static float[] IccO_ToXyz(byte[] profile, int intent, params float[] device)
    {
        var parsed = IccProfile.Parse(profile) ?? throw new Xunit.Sdk.XunitException("Profil unlesbar");
        var xyz = new float[3];
        parsed.ForIntent(intent).ToXyz(device, xyz);
        return xyz;
    }

    /// <summary>Sample device values for n channels: black, white, three mixtures.</summary>
    private static float[][] IccO_Samples(int n) =>
    [
        [.. Enumerable.Repeat(0f, n)], [.. Enumerable.Repeat(1f, n)],
        [.. Enumerable.Range(0, n).Select(i => (float)((i * 0.37 + 0.3) % 1))], [.. Enumerable.Range(0, n).Select(i => (float)((i * 0.61 + 0.15) % 1))],
        [.. Enumerable.Range(0, n).Select(i => i % 2 == 0 ? 0.8f : 0.1f)],
    ];

    /// <summary>Both profiles give the same XYZ for the samples under all four intents.</summary>
    private static void IccO_SameColours(byte[] expected, byte[] actual, int channels)
    {
        for (var intent = 0; intent < 4; intent++)
            foreach (var s in IccO_Samples(channels))
                Assert.Equal(IccO_ToXyz(expected, intent, s), IccO_ToXyz(actual, intent, s));
    }

    /// <summary>The profile gives the XYZ of the formula (within 1e-4) under the intent.</summary>
    private static void IccO_Gives(byte[] profile, int intent, int channels, Func<double[], (double X, double Y, double Z)> formula)
    {
        foreach (var s in IccO_Samples(channels))
        {
            var (x, y, z) = formula([.. s.Select(v => (double)v)]);
            var xyz = IccO_ToXyz(profile, intent, s);
            Assert.True(Math.Abs(xyz[0] - x) < 1e-4 && Math.Abs(xyz[1] - y) < 1e-4 && Math.Abs(xyz[2] - z) < 1e-4,
                $"Absicht {intent}, {string.Join(' ', s)}: {string.Join(' ', xyz)} statt {x} {y} {z}");
        }
    }

    /// <summary>The colour space of an ICCBased array with /N and /Alternate (null: none).</summary>
    private static ColorSpace IccO_Space(byte[] profile, int n, string? alternate)
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var dict = TestPdf.Dict(("N", PdfInteger.Of(n)));
        if (alternate is not null) dict.Set(PdfName.Get("Alternate"), TestPdf.N(alternate));
        return ColorSpace.Create(new PdfArray([TestPdf.N("ICCBased"), new PdfStream(dict, profile)]), null, doc);
    }

    /// <summary>The simple CMYK formula baked as the product does for test profiles (lut16, 17⁴ points, PCS Lab).</summary>
    private static readonly Lazy<byte[]> IccO_NaiveCmyk = new(() => CmykProfile.Bake((c, m, y, k) => ((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k))));

    /// <summary>sRGB of the samples through a profile (no black point compensation).</summary>
    private static float[][] IccO_Srgb(byte[] profile, params float[][] device)
    {
        var transform = new IccTransform(IccProfile.Parse(profile)!, compensateBlack: false);
        return [.. device.Select(d => { var rgb = new float[3]; transform.ToSrgb(d, rgb); return rgb; })];
    }

    /// <summary>The own and bundled profiles PDF-Knead writes, by name.</summary>
    private static IEnumerable<(string Name, byte[] Data)> IccO_WrittenProfiles() =>
    [
        ("Srgb", CalibratedProfile.Srgb()), ("SrgbGray", CalibratedProfile.SrgbGray()), ("Gray", CalibratedProfile.Gray(null)),
        ("Rgb", CalibratedProfile.Rgb(null)), ("RommRgb", CalibratedProfile.RommRgb()),
        ("Primaries", CalibratedProfile.Primaries((0.64, 0.33), (0.30, 0.60), (0.15, 0.06), (0.3127, 0.3290), "Testprimaerfarben")),
        ("AsVersion2", CalibratedProfile.AsVersion2(PdfARuleCases.Version4Rgb())!), ("CmykProfile.Bytes", CmykProfile.Bytes()),
    ];

    private static string IccO_Class(byte[] profile) => Encoding.ASCII.GetString(profile, 12, 4);

    // ------------------------------------------------------------ 8.2 all classes

    /// <summary>
    /// ICC.1, 8.2 sentence 1: every profile PDF-Knead writes (the calibrated ones, sRGB, grey, ROMM, the primaries of JPEG 2000, the rewriting
    /// of a version-4 profile as 2.1, the bundled CMYK profile) has a description, a copyright and a media white point in its tag table.
    /// </summary>
    [Fact]
    public void Icc8_2_WrittenProfiles_CarryDescriptionCopyrightAndWhitePoint()
    {
        foreach (var (name, data) in IccO_WrittenProfiles())
        {
            var tags = IccO_Tags(data).Select(t => t.Sig).ToHashSet();
            foreach (var required in new[] { "desc", "cprt", "wtpt" }) Assert.True(tags.Contains(required), $"{name}: {required} fehlt");
        }
    }

    /// <summary>
    /// ICC.1, 8.2 sentence 4 and 9.2.15 sentences 1–3: rewriting a version-4 matrix profile as 2.1 keeps its chromatic adaptation tag (type
    /// s15Fixed16Array, D65 → D50) byte for byte.
    /// </summary>
    [Fact]
    public void Icc8_2_AsVersion2_KeepsTheChromaticAdaptationByteForByte()
    {
        var v4 = PdfARuleCases.Version4Rgb();
        var source = IccO_TagData(v4, "chad");
        Assert.NotNull(source);
        Assert.StartsWith("sf32", Encoding.ASCII.GetString(source, 0, 4));
        Assert.Equal(source, IccO_TagData(CalibratedProfile.AsVersion2(v4)!, "chad"));
    }

    /// <summary>
    /// ICC.1, 8.2 sentence 6: the own profiles are display profiles of version 2.1 with picture-referred colorimetry and carry no colorimetric
    /// intent image state tag.
    /// </summary>
    [Fact]
    public void Icc8_2_OwnProfiles_DisplayClassWithoutImageStateTag()
    {
        foreach (var data in new[] { CalibratedProfile.Srgb(), CalibratedProfile.Gray(null), CalibratedProfile.RommRgb() })
        {
            Assert.Equal("mntr", IccO_Class(data));
            Assert.Equal(2, data[8]);
            Assert.DoesNotContain(IccO_Tags(data), t => t.Sig == "ciis");
        }
    }

    /// <summary>
    /// ICC.1, 8.2 sentence 7: without an image state tag the PCS colorimetry counts as picture-referred — a CMYK profile with and without a
    /// ciis tag ('scoe') gives the same sRGB for paper, full cyan and a mixture; the reader never looks at the tag.
    /// </summary>
    [Fact]
    public void Icc8_2_ImageStateTag_DoesNotChangeTheColours()
    {
        byte[] ciis = [.. "sig "u8, 0, 0, 0, 0, .. "scoe"u8];
        var with = IccO_With(IccO_NaiveCmyk.Value, [("ciis", ciis)]);
        float[][] samples = [[0, 0, 0, 0], [1, 0, 0, 0], [0.3f, 0.6f, 0.1f, 0.2f]];
        var plain = IccO_Srgb(IccO_NaiveCmyk.Value, samples);
        var tagged = IccO_Srgb(with, samples);
        for (var i = 0; i < samples.Length; i++) Assert.Equal(plain[i], tagged[i]);
    }

    // ------------------------------------------------------------ 8.3–8.7 tables beside the required tags

    /// <summary>
    /// ICC.1, 8.3.2 sentence 4, 8.4.2 sentence 4, 8.5.2 sentence 3, 8.7 sentences 7–8, 8.10.2 step a, 9.1 sentences 46–55 and 9.2.25: a
    /// floating-point table D2B0 beside A2B0 (here meaningless content of type 'mpet') changes nothing for an input, display, output or colour
    /// space profile — the reader does not use D2Bx, the A2B table applies.
    /// </summary>
    [Theory]
    [InlineData("scnr")]
    [InlineData("mntr")]
    [InlineData("prtr")]
    [InlineData("spac")]
    public void Icc8_DToBTagBesideTheTable_ColoursFromTheTable(string deviceClass)
    {
        var plain = IccO_Cmyk(deviceClass, ("A2B0", IccO_Plain));
        var with = IccO_With(plain, [("D2B0", IccO_Junk("mpet", 96))]);
        IccO_Gives(with, 0, 4, IccO_Plain);
        IccO_SameColours(plain, with, 4);
    }

    /// <summary>
    /// ICC.1, 8.3.3 sentence 2: a matrix profile needs all three tone curves — the same matrix profile without bTRC is unreadable, and an
    /// ICCBased space with it takes its /Alternate (DeviceGray).
    /// </summary>
    [Fact]
    public void Icc8_3_3_MatrixProfileWithoutBlueCurve_Unreadable_AlternateApplies()
    {
        Assert.NotNull(IccProfile.Parse(IccO_Matrix("scnr")));
        var without = IccO_With(IccO_Matrix("scnr"), [], "bTRC");
        Assert.Null(IccProfile.Parse(without));
        Assert.Same(ColorSpace.DeviceGray, IccO_Space(without, 3, "DeviceGray"));
    }

    /// <summary>
    /// ICC.1, 8.3.3 sentences 3–4 and 8.4.3 sentences 2–3: an input or display matrix profile with an additional A2B0 of another formula takes
    /// the table, both perceptual and relative colorimetric (no A2B1: A2B0 is the default) — not matrix and curves.
    /// </summary>
    [Theory]
    [InlineData("scnr")]
    [InlineData("mntr")]
    public void Icc8_3_3_MatrixProfileWithA2B0_TableApplies(string deviceClass)
    {
        static (double, double, double) Formula(double[] v) => (0.3 * v[0] + 0.1, 0.4 * v[1] + 0.05, 0.5 * v[2] + 0.02);
        var profile = IccO_With(IccO_Matrix(deviceClass), [("A2B0", IccO_XyzTable(3, Formula))]);
        IccO_Gives(profile, 0, 3, Formula);
        IccO_Gives(profile, 1, 3, Formula);
    }

    /// <summary>
    /// ICC.1, 8.3.3 sentences 7–8, 10 and 8.4.3 sentences 8–9, 11 (writer): the own matrix and grey profiles name the PCS XYZ in the header
    /// (bytes 20–23).
    /// </summary>
    [Fact]
    public void Icc8_3_3_OwnMatrixAndGreyProfiles_PcsXyz()
    {
        foreach (var data in new[] { CalibratedProfile.Srgb(), CalibratedProfile.RommRgb(), CalibratedProfile.Gray(null) })
            Assert.Equal("XYZ ", Encoding.ASCII.GetString(data, 20, 4));
    }

    /// <summary>
    /// ICC.1, 8.3.3 sentences 7–8, 10 and 8.4.3 sentences 8–9, 11 (reader, behaviour of today): a matrix profile whose header names the PCS Lab
    /// is still read — the matrix way does not check the PCS (reported leniency).
    /// </summary>
    [Fact]
    public void Icc8_3_3_MatrixProfileWithPcsLab_ReadToday()
    {
        var lab = IccO_Matrix();
        BinaryPrimitives.WriteUInt32BigEndian(lab.AsSpan(20), IccO_PcsLab);
        Assert.NotNull(IccProfile.Parse(lab));
    }

    /// <summary>
    /// ICC.1, 8.3.4 sentences 2–3, 8.4.4 sentences 2–3 and 8.5.3 sentences 2–3: a grey profile (input, display, output) with a linear grey curve
    /// and an A2B0 (lut16, one input, half the lightness) takes the table: white gives L* 50, not 100; mid grey L* 25, not 50.
    /// </summary>
    [Theory]
    [InlineData("scnr")]
    [InlineData("mntr")]
    [InlineData("prtr")]
    public void Icc8_3_4_GreyProfileWithA2B0_TableApplies(string deviceClass)
    {
        var profile = IccO_Profile(deviceClass, IccProfile.GraySpace, IccO_PcsLab,
            [("wtpt", IccO_XyzTag(0.9642, 1, 0.8249)), ("kTRC", IccO_Gamma(1)), ("A2B0", IccO_Lut16(1, 3, 2, v => IccO_LabRaw(50 * v[0], 0, 0)))]);
        foreach (var intent in new[] { 0, 1 })
        {
            Assert.InRange(IccO_ToXyz(profile, intent, 1)[1], 0.175f, 0.195f);    // L* 50
            Assert.InRange(IccO_ToXyz(profile, intent, 0.5f)[1], 0.040f, 0.050f); // L* 25
        }
    }

    /// <summary>
    /// ICC.1, 8.5.2 sentence 1 (writer): the bundled CMYK profile (output intent of PDF/A, profile of exported CMYK images) is an output profile
    /// with A2B0–A2B2, B2A0–B2A2, gamut tag, description, copyright and white point.
    /// </summary>
    [Fact]
    public void Icc8_5_2_BundledCmykProfile_CarriesTheTagsOfAnOutputProfile()
    {
        var data = CmykProfile.Bytes();
        Assert.Equal("prtr", IccO_Class(data));
        var tags = IccO_Tags(data).Select(t => t.Sig).ToHashSet();
        foreach (var required in new[] { "A2B0", "A2B1", "A2B2", "B2A0", "B2A1", "B2A2", "gamt", "desc", "cprt", "wtpt" })
            Assert.True(tags.Contains(required), $"{required} fehlt");
    }

    /// <summary>
    /// ICC.1, 8.5.2 sentence 2: a 5CLR profile with an A2B0 of five inputs and a colorant table is read with five channels, the colours come
    /// from A2B0, the colorant table has no influence.
    /// </summary>
    [Fact]
    public void Icc8_5_2_FiveColourProfile_ColourantTableWithoutInfluence()
    {
        static (double, double, double) Formula(double[] v) => (0.9 * (1 - v[0]) * (1 - v[4]), 0.8 * (1 - v[1]) * (1 - v[3]), 0.7 * (1 - v[2]));
        var plain = IccO_Profile("prtr", 0x35434C52, IccO_PcsXyz, [("wtpt", IccO_XyzTag(0.9642, 1, 0.8249)), ("A2B0", IccO_XyzTable(5, Formula))]);
        var with = IccO_With(plain, [("clrt", IccO_Junk("clrt", 8 + 4 + 5 * 38))]);
        Assert.Equal(5, IccProfile.Parse(with)!.Channels);
        IccO_Gives(with, 0, 5, Formula);
        IccO_SameColours(plain, with, 5);
    }

    // ------------------------------------------------------------ 8.6–8.9 classes PDF-Knead does not use

    /// <summary>
    /// ICC.1, 8.6 sentences 1–10: a DeviceLink profile (RGB → CMYK, the PCS field names CMYK) is neither a valid ICCBased profile for the PDF/A
    /// check nor readable: the space takes its /Alternate (DeviceGray).
    /// </summary>
    [Fact]
    public void Icc8_6_DeviceLinkToCmyk_RejectedByTheCheck_AlternateApplies()
    {
        var link = IccO_Profile("link", IccProfile.RgbSpace, IccProfile.CmykSpace,
            [("desc", IccO_Text("Link")), ("cprt", IccO_Text("frei")), ("pseq", IccO_Junk("pseq")), ("A2B0", IccO_Lut16(3, 4, 2, v => [v[0], v[1], v[2], 0.5]))]);
        Assert.False(PdfACheck.IccProfileHeaderValid(link, 4));
        Assert.Null(IccProfile.Parse(link));
        Assert.Same(ColorSpace.DeviceGray, IccO_Space(link, 3, "DeviceGray"));
    }

    /// <summary>
    /// ICC.1, 8.6 sentences 9–19 and 8.10.3 (reader, behaviour of today): a DeviceLink profile RGB → Lab with an A2B0 in an ICCBased space is
    /// read like a device profile instead of falling back to /Alternate — the reader does not look at the class (reported, light); the PDF/A
    /// check rejects it.
    /// </summary>
    [Fact]
    public void Icc8_6_DeviceLinkToLab_ReadLikeADeviceProfileToday()
    {
        var link = IccO_Profile("link", IccProfile.RgbSpace, IccO_PcsLab,
            [("desc", IccO_Text("Link")), ("cprt", IccO_Text("frei")), ("A2B0", IccO_Lut16(3, 3, 2, v => IccO_LabRaw(100 * v[1], 0, 0)))]);
        Assert.False(PdfACheck.IccProfileHeaderValid(link, 4));
        Assert.NotNull(IccProfile.Parse(link));
        Assert.NotSame(ColorSpace.DeviceGray, IccO_Space(link, 3, "DeviceGray"));
    }

    /// <summary>
    /// ICC.1, 8.7 sentences 1 and 5: a colour space profile (RGB, PCS Lab, A2B0 of a formula) is read in an ICCBased space and as the profile of
    /// a JPEG 2000 image, the colours come from A2B0, and the PDF/A check accepts its header.
    /// </summary>
    [Fact]
    public void Icc8_7_ColourSpaceProfile_ReadFromItsTable_AcceptedByTheCheck()
    {
        var spac = IccO_Profile("spac", IccProfile.RgbSpace, IccO_PcsLab,
            [("desc", IccO_Text("Raum")), ("cprt", IccO_Text("frei")), ("wtpt", IccO_XyzTag(0.9642, 1, 0.8249)),
                ("A2B0", IccO_Lut16(3, 3, 2, v => IccO_LabRaw(30 + 40 * v[0], 20 * v[1] - 10, 0))), ("B2A0", IccO_Junk("mft2", 64))]);
        Assert.True(PdfACheck.IccProfileHeaderValid(spac, 4));
        foreach (var s in IccO_Samples(3))
        {
            var expected = new float[3];
            IccColor.LabToXyz(30 + 40 * s[0], 20 * s[1] - 10, 0, expected);
            var xyz = IccO_ToXyz(spac, 0, s);
            for (var c = 0; c < 3; c++) Assert.InRange(xyz[c] - expected[c], -2e-3f, 2e-3f);
        }
        var space = IccO_Space(spac, 3, "DeviceGray");
        Assert.NotSame(ColorSpace.DeviceGray, space);
        var transform = new IccTransform(IccProfile.Parse(spac)!);
        Assert.Equal(transform.ToRgb([0.5, 0.2, 0.9]), space.ToRgb([0.5, 0.2, 0.9]));
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        Assert.NotNull(ColorSpace.FromProfile(spac, 3, doc));
    }

    /// <summary>
    /// ICC.1, 8.7 sentence 6: the device fields of the header (manufacturer, model, attributes, bytes 48–63) do not change the colours.
    /// </summary>
    [Fact]
    public void Icc8_7_DeviceFieldsOfTheHeader_DoNotChangeTheColours()
    {
        var changed = (byte[])IccO_NaiveCmyk.Value.Clone();
        for (var i = 48; i < 64; i++) changed[i] = (byte)(0xA5 ^ i);
        float[][] samples = [[0, 0, 0, 0], [1, 0, 0, 0], [0.3f, 0.6f, 0.1f, 0.2f], [0, 0, 0, 1]];
        var plain = IccO_Srgb(IccO_NaiveCmyk.Value, samples);
        var other = IccO_Srgb(changed, samples);
        for (var i = 0; i < samples.Length; i++) Assert.Equal(plain[i], other[i]);
    }

    /// <summary>
    /// ICC.1, 8.8 sentences 1–5 and 8.10.3 (reader, behaviour of today): an abstract profile Lab → Lab in an ICCBased space with /N 3 is read as
    /// Lab data instead of falling back to /Alternate — the reader does not look at the class (reported, light).
    /// </summary>
    [Fact]
    public void Icc8_8_AbstractProfile_ReadAsLabDataToday()
    {
        var abst = IccO_Profile("abst", IccO_PcsLab, IccO_PcsLab,
            [("desc", IccO_Text("Abstrakt")), ("cprt", IccO_Text("frei")), ("wtpt", IccO_XyzTag(0.9642, 1, 0.8249)), ("A2B0", IccO_Lut16(3, 3, 2, v => v))]);
        Assert.False(PdfACheck.IccProfileHeaderValid(abst, 4));
        Assert.NotNull(IccProfile.Parse(abst));
        Assert.NotSame(ColorSpace.DeviceGray, IccO_Space(abst, 3, "DeviceGray"));
    }

    /// <summary>
    /// ICC.1, 8.9 sentences 1–12 and 9.2.37: a named colour profile (RGB, PCS Lab) with only a namedColor2 tag is unreadable — the ICCBased space
    /// takes /Alternate DeviceRGB — and the PDF/A check rejects its header.
    /// </summary>
    [Fact]
    public void Icc8_9_NamedColourProfile_Unreadable_RejectedByTheCheck()
    {
        var nmcl = IccO_Profile("nmcl", IccProfile.RgbSpace, IccO_PcsLab,
            [("desc", IccO_Text("Benannt")), ("cprt", IccO_Text("frei")), ("wtpt", IccO_XyzTag(0.9642, 1, 0.8249)), ("ncl2", IccO_Junk("ncl2", 84 + 38))]);
        Assert.Null(IccProfile.Parse(nmcl));
        Assert.Same(ColorSpace.DeviceRgb, IccO_Space(nmcl, 3, "DeviceRGB"));
        Assert.False(PdfACheck.IccProfileHeaderValid(nmcl, 4));
    }

    // ------------------------------------------------------------ 8.10 precedence, 9.1

    /// <summary>
    /// ICC.1, 8.10.2 step b, 9.1 sentence 38 and 9.2.3: a CMYK profile with A2B0, A2B1 and A2B2 of three formulas — the saturation intent takes
    /// A2B2 (also through <see cref="ColorSpace.WithRendering"/>), perceptual A2B0, relative and absolute colorimetric A2B1.
    /// </summary>
    [Fact]
    public void Icc8_10_2_ThreeTables_EachIntentItsOwn()
    {
        var profile = IccO_Cmyk("prtr", ("A2B0", IccO_Plain), ("A2B1", IccO_Swapped), ("A2B2", IccO_Third));
        IccO_Gives(profile, 0, 4, IccO_Plain);
        IccO_Gives(profile, 1, 4, IccO_Swapped);
        IccO_Gives(profile, 2, 4, IccO_Third);
        IccO_Gives(profile, 3, 4, IccO_Swapped);
        var space = IccO_Space(profile, 4, null);
        var saturation = space.WithRendering(ColorSpace.Saturation, ColorSpace.BpcOff);
        var third = new IccTransform(IccProfile.Parse(profile)!.ForIntent(2), compensateBlack: false);
        double[] cmyk = [0.2, 0.7, 0.4, 0.1];
        Assert.Equal(third.ToRgb(cmyk), saturation.ToRgb(cmyk));
        Assert.NotEqual(new IccTransform(IccProfile.Parse(profile)!.ForIntent(0), compensateBlack: false).ToRgb(cmyk), saturation.ToRgb(cmyk));
    }

    /// <summary>
    /// ICC.1, 8.10.2 step d: a matrix profile that has only an A2B1 table — perceptual and saturation find neither their own table nor A2B0 and
    /// fall to matrix and curves; only the colorimetric intents use A2B1. Before, all intents took A2B1 (docs/FEHLERGEDAECHTNIS.md, No. 1126).
    /// </summary>
    [Fact]
    public void Icc8_10_2_MatrixProfileWithOnlyA2B1_PerceptualTakesMatrixAndCurves()
    {
        static (double, double, double) Formula(double[] v) => (0.3 * v[0] + 0.1, 0.4 * v[1] + 0.05, 0.5 * v[2] + 0.02);
        var matrix = IccO_Matrix();
        var profile = IccO_With(matrix, [("A2B1", IccO_XyzTable(3, Formula))]);
        IccO_Gives(profile, 1, 3, Formula);
        foreach (var intent in new[] { 0, 2 })
            foreach (var s in IccO_Samples(3))
                Assert.Equal(IccO_ToXyz(matrix, intent, s), IccO_ToXyz(profile, intent, s));
    }

    /// <summary>
    /// ICC.1, 9.1 sentence 2 and 10.13.1 sentences 1–6: the type signature at the start of a tag decides — an A2B1 of the type 'mBA ' (a table
    /// of the other direction) beside a valid A2B0 is passed over, the colours come from A2B0.
    /// </summary>
    [Fact]
    public void Icc9_1_TableOfAnUnknownType_PassedOver()
    {
        var plain = IccO_Cmyk("prtr", ("A2B0", IccO_Plain));
        var with = IccO_With(plain, [("A2B1", IccO_Junk("mBA ", 64))]);
        IccO_Gives(with, 1, 4, IccO_Plain);
        IccO_SameColours(plain, with, 4);
    }

    /// <summary>
    /// ICC.1, 9.1 sentences 11–13 (writer): the own profiles contain only the tags their class defines — description, white point, copyright and
    /// the colorants with tone curves, or the grey curve; nothing undefined.
    /// </summary>
    [Fact]
    public void Icc9_1_OwnProfiles_OnlyTheirDefinedTags()
    {
        string[] matrix = ["desc", "wtpt", "cprt", "rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC"];
        Assert.Equal(matrix.Order(), IccO_Tags(CalibratedProfile.Srgb()).Select(t => t.Sig).Order());
        Assert.Equal(matrix.Order(), IccO_Tags(CalibratedProfile.RommRgb()).Select(t => t.Sig).Order());
        Assert.Equal(new[] { "cprt", "desc", "kTRC", "wtpt" }, IccO_Tags(CalibratedProfile.Gray(null)).Select(t => t.Sig).Order());
    }

    /// <summary>
    /// ICC.1, 9.1 sentences 11–13 (reader): a matrix profile of the output class (where the table of intents leaves matrix and curves undefined)
    /// is read like a display profile — its use is left to the implementation.
    /// </summary>
    [Fact]
    public void Icc9_1_MatrixProfileOfTheOutputClass_ReadLikeADisplayProfile()
    {
        IccO_SameColours(IccO_Matrix("mntr"), IccO_Matrix("prtr"), 3);
        Assert.True(IccProfile.Parse(IccO_Matrix("prtr"))!.IsMatrixShaper);
    }
}
