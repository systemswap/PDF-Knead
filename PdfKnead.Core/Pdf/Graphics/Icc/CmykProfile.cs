using System.Buffers.Binary;

namespace PdfKnead.Core.Pdf.Graphics.Icc;

/// <summary>
/// CMYK without a profile of its own (DeviceCMYK, no output profile in the document): the bundled free profile "FOGRA39L coated"
/// (basICColor/colord, zlib licence — folder Farbprofile/, licence in packaging/licenses). Common PDF editors with German settings
/// (Coated FOGRA39) convert the same way. The SWOP approximation used by some readers is not used because its shadows are bluish.
/// </summary>
internal static class CmykProfile
{
    private const string Resource = "Farbprofile/FOGRA39L_coated.icc";

    /// <summary>The name of the bundled profile (the printing condition it describes), as an output intent names it.</summary>
    public const string Name = "FOGRA39L coated (ISO 12647-2)";
    private const int Points = 17;

    private static readonly Lazy<IccTransform> LazyDefault = new(() =>
    {
        using var stream = typeof(CmykProfile).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("Farbprofil fehlt: " + Resource);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return new IccTransform(IccProfile.Parse(copy.ToArray()) ?? throw new InvalidOperationException("Farbprofil unlesbar: " + Resource));
    });

    /// <summary>Transform of the bundled profile (with black point compensation); loaded on first use.</summary>
    public static IccTransform Default => LazyDefault.Value;

    /// <summary>The bundled profile as a file (for exported CMYK images).</summary>
    public static byte[] Bytes()
    {
        using var stream = typeof(CmykProfile).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("Farbprofil fehlt: " + Resource);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// Builds a CMYK profile (lut16, 17⁴ points, PCS Lab) from a conversion CMYK 0…1 → sRGB 0…1 — for profiles of test files. (Until FG 805 it
    /// also baked the conversion for CMYK JPEGs that Skia decoded; the own decoder needs no such detour.)
    /// </summary>
    public static byte[] Bake(Func<double, double, double, double, (double R, double G, double B)> toSrgb)
    {
        // header (128) + tag table (4 + 2 × 12) + wtpt (20) + A2B0 (lut16)
        const int count = Points * Points * Points * Points;
        const int lutSize = 52 + 4 * 2 * 2 + count * 3 * 2 + 3 * 2 * 2;
        const int wtpt = 128 + 4 + 2 * 12;
        const int lut = wtpt + 20;
        var size = lut + lutSize;
        var d = new byte[size];

        void U32(int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(at), v);
        void U16(int at, int v) => BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(at), (ushort)v);
        void S15(int at, double v) => BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(at), (int)Math.Round(v * 65536));

        U32(0, (uint)size);
        U32(8, 0x02100000);                       // Version 2.1
        U32(12, 0x70727472);                      // prtr
        U32(16, IccProfile.CmykSpace);
        U32(20, IccProfile.LabSpace);
        U32(36, 0x61637370);                      // acsp
        S15(68, IccProfile.WhiteX);
        S15(72, IccProfile.WhiteY);
        S15(76, IccProfile.WhiteZ);
        U32(128, 2);
        U32(132, 0x77747074); U32(136, wtpt); U32(140, 20);         // wtpt
        U32(144, 0x41324230); U32(148, lut); U32(152, lutSize);     // A2B0
        U32(wtpt, 0x58595A20);
        S15(wtpt + 8, IccProfile.WhiteX);
        S15(wtpt + 12, IccProfile.WhiteY);
        S15(wtpt + 16, IccProfile.WhiteZ);

        // lut16: 4 inputs, 3 outputs, identity matrix, curves with two points (identity)
        U32(lut, 0x6D667432);
        d[lut + 8] = 4;
        d[lut + 9] = 3;
        d[lut + 10] = Points;
        S15(lut + 12, 1);
        S15(lut + 28, 1);
        S15(lut + 44, 1);
        U16(lut + 48, 2);
        U16(lut + 50, 2);
        var at = lut + 52;
        for (var i = 0; i < 4; i++, at += 4)
        {
            U16(at, 0);
            U16(at + 2, 65535);
        }
        // table: C varies slowest, K fastest; Lab in the legacy 16-bit encoding (L 100 = 0xFF00)
        var table = at;
        double Value(int point) => point / (Points - 1.0);
        Parallel.For(0, count, i =>
        {
            int c = i / (Points * Points * Points), m = i / (Points * Points) % Points, y = i / Points % Points, k = i % Points;
            var (r, g, b) = toSrgb(Value(c), Value(m), Value(y), Value(k));
            Span<float> xyz = stackalloc float[3];
            IccColor.SrgbToXyz((float)r, (float)g, (float)b, xyz);
            var (l, la, lb) = IccColor.XyzToLab(xyz[0], xyz[1], xyz[2]);
            var o = d.AsSpan(table + 6 * i);
            BinaryPrimitives.WriteUInt16BigEndian(o, (ushort)Math.Clamp((int)Math.Round(l / 100 * 65280), 0, 65535));
            BinaryPrimitives.WriteUInt16BigEndian(o[2..], (ushort)Math.Clamp((int)Math.Round((la + 128) * 256), 0, 65535));
            BinaryPrimitives.WriteUInt16BigEndian(o[4..], (ushort)Math.Clamp((int)Math.Round((lb + 128) * 256), 0, 65535));
        });
        at = table + 6 * count;
        for (var i = 0; i < 3; i++, at += 4)
        {
            U16(at, 0);
            U16(at + 2, 65535);
        }
        return d;
    }
}
