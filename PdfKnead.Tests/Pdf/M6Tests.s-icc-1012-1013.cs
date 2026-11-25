using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M6 draft s-icc-1012-1013 (ICC.1:2022, 10.12 lutAToBType, 10.13 lutBToAType). The tables are built here (prefix IccS_), so that the proofs
/// do not depend on the local reference profiles; the profile builders come from the file of draft o (prefix IccO_).
/// </summary>
public sealed partial class M6NormTests
{
    /// <summary>
    /// A lutAToBType: header (signature, reserved, inputs, outputs, padding, the five offsets B, matrix, M, CLUT, A) and the elements given,
    /// each at a multiple of four bytes; the same array given twice is placed once (shared data), a missing element has the offset 0.
    /// </summary>
    private static byte[] IccS_Mab(int inputs, int outputs, byte[]? b, byte[]? matrix, byte[]? m, byte[]? clut, byte[]? a, string signature = "mAB ")
    {
        var body = new MemoryStream();
        body.Write(new byte[32]);
        var placed = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
        var offsets = new int[5];
        byte[]?[] elements = [b, matrix, m, clut, a];
        for (var i = 0; i < 5; i++)
        {
            if (elements[i] is not { } e) continue;
            if (!placed.TryGetValue(e, out var at))
            {
                while (body.Length % 4 != 0) body.WriteByte(0);
                at = (int)body.Length;
                body.Write(e);
                placed[e] = at;
            }
            offsets[i] = at;
        }
        while (body.Length % 4 != 0) body.WriteByte(0);
        var d = body.ToArray();
        Encoding.ASCII.GetBytes(signature, d.AsSpan(0, 4));
        d[8] = (byte)inputs;
        d[9] = (byte)outputs;
        for (var i = 0; i < 5; i++) BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(12 + 4 * i), (uint)offsets[i]);
        return d;
    }

    /// <summary>Curves one after the other, each padded with zero bytes to a multiple of four.</summary>
    private static byte[] IccS_Curves(params byte[][] curves)
    {
        var list = new List<byte>();
        foreach (var c in curves)
        {
            list.AddRange(c);
            while (list.Count % 4 != 0) list.Add(0);
        }
        return [.. list];
    }

    /// <summary>Three identity curves (curveType without entries).</summary>
    private static byte[] IccS_Identity3() => IccS_Curves(IccO_Curv(), IccO_Curv(), IccO_Curv());

    /// <summary>The matrix element: nine numbers row by row and three offsets (s15Fixed16Number).</summary>
    private static byte[] IccS_Matrix(double[] rows, double[]? offset = null)
    {
        var d = new byte[48];
        for (var i = 0; i < 12; i++)
            BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(4 * i), (int)Math.Round((i < 9 ? rows[i] : offset?[i - 9] ?? 0) * 65536));
        return d;
    }

    private static readonly double[] IccS_Unit = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    /// <summary>
    /// The CLUT element: grid points per dimension (bytes 0–15), precision (byte 16), padding, then the values (first dimension varying slowest);
    /// <paramref name="value"/> gives the outputs 0…1 at a grid index.
    /// </summary>
    private static byte[] IccS_Clut(int[] grid, int precision, int outputs, Func<int[], double[]> value)
    {
        var points = grid.Aggregate(1, (p, g) => p * g);
        var d = new byte[20 + points * outputs * precision];
        for (var i = 0; i < grid.Length; i++) d[i] = (byte)grid[i];
        d[16] = (byte)precision;
        var at = 20;
        for (var p = 0; p < points; p++)
        {
            var index = new int[grid.Length];
            var rest = p;
            for (var i = grid.Length - 1; i >= 0; i--)
            {
                index[i] = rest % grid[i];
                rest /= grid[i];
            }
            var v = value(index);
            for (var o = 0; o < outputs; o++)
            {
                if (precision == 1) d[at++] = (byte)Math.Round(Math.Clamp(v[o], 0, 1) * 255);
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(at), (ushort)Math.Round(Math.Clamp(v[o], 0, 1) * 65535));
                    at += 2;
                }
            }
        }
        return d;
    }

    /// <summary>The identity CLUT of three inputs and outputs with two points each (16 bit).</summary>
    private static byte[] IccS_UnitClut() => IccS_Clut([2, 2, 2], 2, 3, i => [i[0], i[1], i[2]]);

    /// <summary>A display profile (PCS XYZ unless given) whose A2B0 is the table.</summary>
    private static byte[] IccS_Profile(uint space, byte[] a2b0, uint pcs = IccO_PcsXyz) =>
        IccO_Profile(space == IccProfile.CmykSpace ? "prtr" : "mntr", space, pcs, [("wtpt", IccO_XyzTag(0.9642, 1, 0.8249)), ("A2B0", a2b0)]);

    /// <summary>The table values (0…1) a profile with PCS XYZ gives for device values: its XYZ divided by the u1Fixed15 scale.</summary>
    private static double[] IccS_Values(byte[] profile, params float[] device) => [.. IccO_ToXyz(profile, 0, device).Select(x => x / IccO_XyzScale)];

    // ------------------------------------------------------------ 10.12.1

    /// <summary>
    /// ICC.1, 10.12.1 sentences 8–13 (read leniently as the three reference readers, docs/normabgleich/sonderregeln.md S-A A186): an mAB
    /// without any element (all offsets 0) and one with
    /// the sequence A, M, B (no CLUT, no matrix) are still read — the reader checks neither the minimum nor the allowed sequences; without
    /// elements the device values count as PCS (grey 0.5 gives XYZ about 1.0).
    /// </summary>
    [Fact]
    public void Icc10_12_1_NoElementOrSequenceAMB_ReadLeniently()
    {
        var empty = IccS_Profile(IccProfile.RgbSpace, IccS_Mab(3, 3, null, null, null, null, null));
        Assert.NotNull(IccProfile.Parse(empty));
        foreach (var x in IccO_ToXyz(empty, 0, 0.5f, 0.5f, 0.5f)) Assert.InRange(x, 0.999f, 1.001f);
        var gamma = IccS_Curves(IccO_Gamma(2), IccO_Gamma(2), IccO_Gamma(2));
        var amb = IccS_Profile(IccProfile.RgbSpace, IccS_Mab(3, 3, IccS_Identity3(), null, IccS_Identity3(), null, gamma));
        Assert.NotNull(IccProfile.Parse(amb));
        foreach (var v in IccS_Values(amb, 0.5f, 0.5f, 0.5f)) Assert.InRange(v, 0.2499, 0.2501);
    }

    /// <summary>
    /// ICC.1, 10.12.1 sentences 21–22: inputs and outputs belong to the channels of the colour space and the PCS — an mAB of three inputs in a
    /// CMYK profile, and one of four outputs in an RGB profile, make the profile unreadable; the ICCBased space takes /Alternate DeviceGray.
    /// </summary>
    [Fact]
    public void Icc10_12_1_ChannelsNotMatchingTheSpace_Unreadable()
    {
        var threeIn = IccS_Profile(IccProfile.CmykSpace, IccS_Mab(3, 3, IccS_Identity3(), null, null, IccS_UnitClut(), IccS_Identity3()));
        Assert.Null(IccProfile.Parse(threeIn));
        Assert.Same(ColorSpace.DeviceGray, IccO_Space(threeIn, 4, "DeviceGray"));
        var four = IccS_Curves(IccO_Curv(), IccO_Curv(), IccO_Curv(), IccO_Curv());
        var fourOut = IccS_Profile(IccProfile.RgbSpace, IccS_Mab(3, 4, four, null, null, IccS_Clut([2, 2, 2], 2, 4, i => [i[0], i[1], i[2], 0]), IccS_Identity3()));
        Assert.Null(IccProfile.Parse(fourOut));
        Assert.Same(ColorSpace.DeviceGray, IccO_Space(fourOut, 3, "DeviceGray"));
    }

    /// <summary>
    /// ICC.1, 10.12.1 sentences 33–34: each curve starts at a four-byte boundary — three B curves of three entries (18 bytes and two padding
    /// bytes each), all different: each channel follows its own curve (0.5 gives 0.5, 0.25 and 0.75).
    /// </summary>
    [Fact]
    public void Icc10_12_1_CurvesWithPadding_EachChannelItsCurve()
    {
        var curves = IccS_Curves(IccO_Curv(0, 0.5, 1), IccO_Curv(0, 0.25, 1), IccO_Curv(0, 0.75, 1));
        Assert.Equal(3 * 20, curves.Length);
        var profile = IccS_Profile(IccProfile.RgbSpace, IccS_Mab(3, 3, curves, null, null, null, null));
        var v = IccS_Values(profile, 0.5f, 0.5f, 0.5f);
        Assert.InRange(v[0], 0.4999, 0.5001);
        Assert.InRange(v[1], 0.2499, 0.2501);
        Assert.InRange(v[2], 0.7499, 0.7501);
    }

    /// <summary>
    /// ICC.1, 10.12.1 sentences 35–36: curve data may be shared — A, M and B curves at the same offset (three gamma-2 curves), with an identity
    /// CLUT and an identity matrix: the profile is read and grey 0.5 gives 0.5^8 (gamma 2 three times).
    /// </summary>
    [Fact]
    public void Icc10_12_1_SharedCurveData_ReadThreeTimes()
    {
        var gamma = IccS_Curves(IccO_Gamma(2), IccO_Gamma(2), IccO_Gamma(2));
        var table = IccS_Mab(3, 3, gamma, IccS_Matrix(IccS_Unit), gamma, IccS_UnitClut(), gamma);
        Assert.Equal(BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(12)), BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(28)));
        Assert.Equal(BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(12)), BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(20)));
        var profile = IccS_Profile(IccProfile.RgbSpace, table);
        foreach (var v in IccS_Values(profile, 0.5f, 0.5f, 0.5f)) Assert.InRange(v, Math.Pow(0.5, 8) - 1e-5, Math.Pow(0.5, 8) + 1e-5);
    }

    // ------------------------------------------------------------ 10.12.2–10.12.5

    /// <summary>
    /// ICC.1, 10.12.2 sentence 2 (read leniently as the three reference readers, S-A A186): A curves without a CLUT are still read and applied (gamma 2:
    /// 0.5 gives 0.25).
    /// </summary>
    [Fact]
    public void Icc10_12_2_ACurvesWithoutClut_ReadLeniently()
    {
        var gamma = IccS_Curves(IccO_Gamma(2), IccO_Gamma(2), IccO_Gamma(2));
        var profile = IccS_Profile(IccProfile.RgbSpace, IccS_Mab(3, 3, IccS_Identity3(), null, null, null, gamma));
        Assert.NotNull(IccProfile.Parse(profile));
        foreach (var v in IccS_Values(profile, 0.5f, 0.5f, 0.5f)) Assert.InRange(v, 0.2499, 0.2501);
    }

    /// <summary>
    /// ICC.1, 10.12.3 sentences 1–7: an 8-bit CLUT with a different number of points per dimension (2, 3, 5), each output depending on one
    /// dimension: the values at the grid points come out exactly (byte / 255), between them linearly — the first dimension varies slowest, the
    /// last fastest.
    /// </summary>
    [Fact]
    public void Icc10_12_3_EightBitClutOfDifferentGrids_ExactAtPointsLinearBetween()
    {
        byte[] first = [20, 220], second = [10, 240, 30], third = [0, 200, 50, 255, 100];
        var clut = IccS_Clut([2, 3, 5], 1, 3, i => [first[i[0]] / 255.0, second[i[1]] / 255.0, third[i[2]] / 255.0]);
        var profile = IccS_Profile(IccProfile.RgbSpace, IccS_Mab(3, 3, IccS_Identity3(), null, null, clut, IccS_Identity3()));
        void Check(float x, float y, float z, double a, double b, double c)
        {
            var v = IccS_Values(profile, x, y, z);
            Assert.True(Math.Abs(v[0] - a / 255) < 1e-4 && Math.Abs(v[1] - b / 255) < 1e-4 && Math.Abs(v[2] - c / 255) < 1e-4,
                $"{x} {y} {z}: {string.Join(' ', v.Select(e => e * 255))} statt {a} {b} {c}");
        }
        Check(0, 0, 0, 20, 10, 0);
        Check(1, 0.5f, 0.25f, 220, 240, 200);
        Check(0, 1, 0.75f, 20, 30, 255);
        Check(0.5f, 0.25f, 0.375f, 120, 125, 125);
        Check(0.25f, 0.75f, 0.875f, 70, 135, 177.5);
    }

    /// <summary>
    /// ICC.1, 10.12.3 sentences 13 and 16 (behaviour of today, design decision pending): unused grid point entries (here 7) and padding bytes
    /// other than zero give the same colours as zeros — the reader passes over these fields.
    /// </summary>
    [Fact]
    public void Icc10_12_3_UnusedGridEntriesAndPadding_PassedOver()
    {
        var clut = IccS_Clut([2, 3, 2], 2, 3, i => [i[0] * 0.8, i[1] * 0.4, i[2] * 0.3 + 0.1]);
        var plain = IccS_Profile(IccProfile.RgbSpace, IccS_Mab(3, 3, IccS_Identity3(), null, null, clut, IccS_Identity3()));
        var dirty = (byte[])clut.Clone();
        for (var i = 3; i < 16; i++) dirty[i] = 7;
        dirty[17] = 0x5A;
        dirty[18] = 0xA5;
        dirty[19] = 0xFF;
        var other = IccS_Profile(IccProfile.RgbSpace, IccS_Mab(3, 3, IccS_Identity3(), null, null, dirty, IccS_Identity3()));
        IccO_SameColours(plain, other, 3);
    }

    /// <summary>
    /// ICC.1, 10.12.3 sentence 15: the precision is one or two bytes — with 1 and 2 the profile is read, with 3 it is unreadable and the
    /// ICCBased space takes /Alternate DeviceGray.
    /// </summary>
    [Fact]
    public void Icc10_12_3_PrecisionThree_Unreadable()
    {
        foreach (var precision in new[] { 1, 2 })
            Assert.NotNull(IccProfile.Parse(IccS_Profile(IccProfile.RgbSpace,
                IccS_Mab(3, 3, IccS_Identity3(), null, null, IccS_Clut([2, 2, 2], precision, 3, i => [i[0], i[1], i[2]]), IccS_Identity3()))));
        var clut = IccS_UnitClut();
        clut[16] = 3;
        var three = IccS_Profile(IccProfile.RgbSpace, IccS_Mab(3, 3, IccS_Identity3(), null, null, clut, IccS_Identity3()));
        Assert.Null(IccProfile.Parse(three));
        Assert.Same(ColorSpace.DeviceGray, IccO_Space(three, 3, "DeviceGray"));
    }

    /// <summary>
    /// ICC.1, 10.12.3 sentence 19: with four inputs and three outputs the CLUT is required — a CMYK mAB without CLUT makes the profile
    /// unreadable, the ICCBased space takes its /Alternate.
    /// </summary>
    [Fact]
    public void Icc10_12_3_DifferentChannelCountsWithoutClut_Unreadable()
    {
        var four = IccS_Curves(IccO_Curv(), IccO_Curv(), IccO_Curv(), IccO_Curv());
        var profile = IccS_Profile(IccProfile.CmykSpace, IccS_Mab(4, 3, IccS_Identity3(), null, null, null, four));
        Assert.Null(IccProfile.Parse(profile));
        Assert.Same(ColorSpace.DeviceGray, IccO_Space(profile, 4, "DeviceGray"));
    }

    /// <summary>
    /// ICC.1, 10.12.4 sentence 6 (read leniently as the three reference readers, S-A A186): M curves without a matrix are still read and applied (gamma 2:
    /// 0.5 gives 0.25).
    /// </summary>
    [Fact]
    public void Icc10_12_4_MCurvesWithoutMatrix_ReadLeniently()
    {
        var gamma = IccS_Curves(IccO_Gamma(2), IccO_Gamma(2), IccO_Gamma(2));
        var profile = IccS_Profile(IccProfile.RgbSpace, IccS_Mab(3, 3, IccS_Identity3(), null, gamma, null, null));
        Assert.NotNull(IccProfile.Parse(profile));
        foreach (var v in IccS_Values(profile, 0.5f, 0.5f, 0.5f)) Assert.InRange(v, 0.2499, 0.2501);
    }

    /// <summary>
    /// ICC.1, 10.12.5 sentence 9: the results of the matrix are limited to 0…1 before the B curves — with e1 = 2 the input (0.75 0 0) gives the
    /// table value 1 (X = 65535/32768), not 1.5.
    /// </summary>
    [Fact]
    public void Icc10_12_5_MatrixResultLimitedToOne()
    {
        var profile = IccS_Profile(IccProfile.RgbSpace, IccS_Mab(3, 3, IccS_Identity3(), IccS_Matrix([2, 0, 0, 0, 1, 0, 0, 0, 1]), IccS_Identity3(), null, null));
        var xyz = IccO_ToXyz(profile, 0, 0.75f, 0, 0);
        Assert.InRange(xyz[0], IccO_XyzScale - 1e-4, IccO_XyzScale + 1e-4);
    }
}
