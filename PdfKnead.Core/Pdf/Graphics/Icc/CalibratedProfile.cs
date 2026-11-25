using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Graphics.Icc;

/// <summary>
/// ICC profiles (version 2.1, ICC.1:2001-04) that describe the CIE-based colour spaces CalGray and CalRGB (ISO 32000-2, 8.6.5.2/8.6.5.3):
/// a grey tone curve, or three tone curves with a matrix to XYZ. An exported image keeps its raw values and carries the profile, so that
/// it shows the colours the PDF means. White point adapted to D50 with Bradford, exactly as these colour spaces are drawn. Also sRGB
/// (IEC 61966-2.1) built the same way from its published definition, as the output profile of PDF/A files (KONZEPT.md, section 5.21).
/// For a file of PDF 2.0 the sRGB profile is written as version 4.3 (ICC.1:2010, the version ISO 32000-2, Table 66 names): description and
/// copyright as multiLocalizedUnicodeType, the chromatic adaptation in chad, the profile ID set — with exactly the same colours; for PDF/A-1 and
/// the versions before 2.0, and for exported images, as version 2.1 (docs/FEHLERGEDAECHTNIS.md, No. 1133).
/// </summary>
internal static class CalibratedProfile
{
    private const string Description = "Calibrated color space from PDF";

    private const string Copyright = "No copyright, use freely";

    /// <summary>The white of sRGB (D65, IEC 61966-2.1: x 0.3127, y 0.3290) as XYZ.</summary>
    private static readonly double[] D65 = [0.3127 / 0.3290, 1, (1 - 0.3127 - 0.3290) / 0.3290];

    /// <summary>The registry name of sRGB, used as the description of <see cref="Srgb"/> and as the output condition of PDF/A files.</summary>
    public const string SrgbName = "sRGB IEC61966-2.1";

    private const int SrgbPoints = 1024;

    private static readonly Lazy<byte[]> SrgbBytes = new(() => SrgbProfile(version4: false));

    private static readonly Lazy<byte[]> Srgb4Bytes = new(() => SrgbProfile(version4: true));

    private static byte[] SrgbProfile(bool version4)
    {
        // the primaries of IEC 61966-2.1 under D65, adapted to D50 with Bradford (the colorants of the published profile)
        var curve = SrgbCurve();
        return Build(IccProfile.RgbSpace,
        [
            ("rXYZ", Xyz(0.4360747f, 0.2225045f, 0.0139322f)),
            ("gXYZ", Xyz(0.3850649f, 0.7168786f, 0.0971045f)),
            ("bXYZ", Xyz(0.1430804f, 0.0606169f, 0.7141733f)),
            ("rTRC", curve), ("gTRC", curve), ("bTRC", curve),
        ], SrgbName, version4, D65);
    }

    /// <summary>
    /// An sRGB profile: the three primaries and the sRGB tone curve as a table of 1024 points. Own data, no profile file of another party.
    /// </summary>
    /// <param name="version4">As version 4.3 for a file of PDF 2.0 (ISO 32000-2, Table 66: ICC.1:2010); else version 2.1 (PDF/A-1, files
    /// before 2.0, exported images).</param>
    /// <returns>A new copy of the profile.</returns>
    public static byte[] Srgb(bool version4 = false) => (byte[])(version4 ? Srgb4Bytes : SrgbBytes).Value.Clone();

    public const string SrgbGrayName = "Grey with the sRGB tone curve";

    private static readonly Lazy<byte[]> SrgbGrayBytes = new(() => Build(IccProfile.GraySpace, [("kTRC", SrgbCurve())], SrgbGrayName));

    /// <summary>
    /// A grey profile with the tone curve of sRGB (IEC 61966-2.1): what PDF-Knead's grey pixels are — rendered or scanned values, not linear
    /// ones. For the TIFF export, where grey without a profile counts as linear (TIFF 6.0, section 17; docs/FEHLERGEDAECHTNIS.md, No. 980).
    /// </summary>
    public static byte[] SrgbGray() => (byte[])SrgbGrayBytes.Value.Clone();

    /// <summary>The registry name of ROMM RGB, the description of <see cref="RommRgb"/>.</summary>
    public const string RommName = "ROMM RGB (ISO 22028-2)";

    private static readonly Lazy<byte[]> RommBytes = new(() =>
    {
        // ISO 22028-2: primaries x, y of red, green and blue, white D50 — the colorants scaled so that they add up to the white of the PCS
        var (r, g, b) = Colorants((0.7347, 0.2653), (0.1596, 0.8404), (0.0366, 0.0001), [IccProfile.WhiteX, IccProfile.WhiteY, IccProfile.WhiteZ]);
        // the tone curve, encoded value E′ → linear E: E′/16 below 16·Et (Et = 0.001953), otherwise E′^1.8 — continuous at 0.03125
        var curve = new byte[12 + ParametricPoints * 2];
        "curv"u8.CopyTo(curve);
        BinaryPrimitives.WriteUInt32BigEndian(curve.AsSpan(8), ParametricPoints);
        for (var i = 0; i < ParametricPoints; i++)
        {
            var v = i / (double)(ParametricPoints - 1);
            var linear = v < 16 * 0.001953 ? v / 16 : Math.Pow(v, 1.8);
            BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(12 + i * 2), (ushort)Math.Round(linear * 65535));
        }
        return Build(IccProfile.RgbSpace,
        [
            ("rXYZ", Xyz(r[0], r[1], r[2])), ("gXYZ", Xyz(g[0], g[1], g[2])), ("bXYZ", Xyz(b[0], b[1], b[2])),
            ("rTRC", curve), ("gTRC", curve), ("bTRC", curve),
        ], RommName);
    });

    /// <summary>
    /// A ROMM RGB profile (ISO 22028-2, the enumerated colour space 21 of JPEG 2000, ISO/IEC 15444-2, Table M.25), built from its definition:
    /// the three primaries under D50 and the tone curve as a table of 4 096 points. Own data, no profile file of another party.
    /// </summary>
    /// <returns>A new copy of the profile.</returns>
    public static byte[] RommRgb() => (byte[])RommBytes.Value.Clone();

    /// <summary>The XYZ of three primaries (chromaticities x, y) that add up to <paramref name="white"/>.</summary>
    internal static (float[] R, float[] G, float[] B) Colorants((double X, double Y) r, (double X, double Y) g, (double X, double Y) b, float[] white)
    {
        var m = ToXyz(r, g, b, [white[0], white[1], white[2]]);
        float[] Column(int c) => [(float)m[0, c], (float)m[1, c], (float)m[2, c]];
        return (Column(0), Column(1), Column(2));
    }

    /// <summary>
    /// RGB → XYZ of three primaries given as chromaticities x, y: the columns x, y, z of each primary, scaled by S = M⁻¹·W so that they add up
    /// to <paramref name="white"/> (XYZ). Unlike scaling to Y = 1 first, this also holds for a primary with y = 0 (the X and Z primaries of
    /// CIE XYZ, ITU-T H.273, Table 2, value 10).
    /// </summary>
    internal static double[,] ToXyz((double X, double Y) r, (double X, double Y) g, (double X, double Y) b, double[] white)
    {
        var m = new double[,] { { r.X, g.X, b.X }, { r.Y, g.Y, b.Y }, { 1 - r.X - r.Y, 1 - g.X - g.Y, 1 - b.X - b.Y } };
        var inverse = Invert(m);
        var result = new double[3, 3];
        for (var c = 0; c < 3; c++)
        {
            var s = inverse[c, 0] * white[0] + inverse[c, 1] * white[1] + inverse[c, 2] * white[2];
            for (var row = 0; row < 3; row++) result[row, c] = m[row, c] * s;
        }
        return result;
    }

    /// <summary>The inverse of a 3 × 3 matrix: the cofactors over the determinant (the cyclic form carries the signs).</summary>
    internal static double[,] Invert(double[,] a)
    {
        var det = a[0, 0] * (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1]) - a[0, 1] * (a[1, 0] * a[2, 2] - a[1, 2] * a[2, 0])
            + a[0, 2] * (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0]);
        var inverse = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
            {
                int r0 = (j + 1) % 3, r1 = (j + 2) % 3, c0 = (i + 1) % 3, c1 = (i + 2) % 3;
                inverse[i, j] = (a[r0, c0] * a[r1, c1] - a[r0, c1] * a[r1, c0]) / det;
            }
        return inverse;
    }

    /// <summary>The product <paramref name="a"/> · <paramref name="b"/> of two 3 × 3 matrices.</summary>
    internal static double[,] Multiply(double[,] a, double[,] b)
    {
        var product = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++) product[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
        return product;
    }

    /// <summary>
    /// The chromatic adaptation from the white <paramref name="from"/> to the white <paramref name="to"/> (XYZ) with Bradford, as a matrix — the
    /// same cone space as the adaptation to D50 of the profiles here, so that adapting with it and then to D50 equals adapting to D50 directly.
    /// </summary>
    internal static double[,] Bradford(double[] from, double[] to)
    {
        var cone = new[,] { { 0.8951, 0.2664, -0.1614 }, { -0.7502, 1.7135, 0.0367 }, { 0.0389, -0.0685, 1.0296 } };
        double[] Cone(double[] w) => [.. Enumerable.Range(0, 3).Select(i => cone[i, 0] * w[0] + cone[i, 1] * w[1] + cone[i, 2] * w[2])];
        var (s, d) = (Cone(from), Cone(to));
        var scale = new double[3, 3];
        for (var i = 0; i < 3; i++) scale[i, i] = d[i] / s[i];
        return Multiply(Invert(cone), Multiply(scale, cone));
    }

    /// <summary>
    /// An RGB profile of three primaries and a white point given as chromaticities x, y (ITU-T H.273, Table 2): the colorants under that white,
    /// adapted to D50 with Bradford as the other profiles here, and the sRGB tone curve — for samples that carry the linear light of these
    /// primaries written with that curve (the Parameterized colour specification of JPEG 2000, ISO/IEC 15444-2, M.11.7.3.4).
    /// </summary>
    /// <returns>A new profile.</returns>
    public static byte[] Primaries((double X, double Y) r, (double X, double Y) g, (double X, double Y) b, (double X, double Y) white, string description)
    {
        float[] w = [(float)(white.X / white.Y), 1f, (float)((1 - white.X - white.Y) / white.Y)];
        var m = ToXyz(r, g, b, [white.X / white.Y, 1, (1 - white.X - white.Y) / white.Y]);
        var curve = SrgbCurve();
        var tags = new List<(string, byte[])>();
        string[] colorants = ["rXYZ", "gXYZ", "bXYZ"];
        for (var c = 0; c < 3; c++)
        {
            Span<float> v = [(float)m[0, c], (float)m[1, c], (float)m[2, c]];
            IccColor.AdaptToD50(w, v);
            tags.Add((colorants[c], Xyz(v[0], v[1], v[2])));
        }
        tags.Add(("rTRC", curve));
        tags.Add(("gTRC", curve));
        tags.Add(("bTRC", curve));
        return Build(IccProfile.RgbSpace, tags, description);
    }

    /// <summary>Profile for CalGray.</summary>
    /// <param name="dict">The dictionary of the colour space (/WhitePoint, /Gamma); null = defaults.</param>
    public static byte[] Gray(PdfDictionary? dict)
    {
        var gamma = dict?.GetNumber(PdfName.Get("Gamma")) ?? 1;
        return Build(IccProfile.GraySpace, [("kTRC", Curve(gamma))]);
    }

    /// <summary>Profile for CalRGB.</summary>
    /// <param name="dict">The dictionary of the colour space (/WhitePoint, /Gamma, /Matrix); null = defaults.</param>
    public static byte[] Rgb(PdfDictionary? dict)
    {
        var white = WhitePoint(dict);
        var gamma = PdfFunction.Numbers(dict?.GetArray(PdfName.Get("Gamma"))) is { Length: 3 } g ? g : [1.0, 1.0, 1.0];
        // /Matrix: XA YA ZA XB YB ZB XC YC ZC — per component the XYZ of its full value: exactly the colorant tags, adapted to D50
        var matrix = PdfFunction.Numbers(dict?.GetArray(PdfName.Get("Matrix"))) is { Length: 9 } m ? m : [1.0, 0, 0, 0, 1, 0, 0, 0, 1];
        var tags = new List<(string, byte[])>();
        string[] colorants = ["rXYZ", "gXYZ", "bXYZ"];
        for (var c = 0; c < 3; c++)
        {
            Span<float> v = [(float)matrix[c * 3], (float)matrix[c * 3 + 1], (float)matrix[c * 3 + 2]];
            IccColor.AdaptToD50(white, v);
            tags.Add((colorants[c], Xyz(v[0], v[1], v[2])));
        }
        tags.Add(("rTRC", Curve(gamma[0])));
        tags.Add(("gTRC", Curve(gamma[1])));
        tags.Add(("bTRC", Curve(gamma[2])));
        return Build(IccProfile.RgbSpace, tags);
    }

    /// <summary>Points of a tone curve given as a formula (parametricCurveType), written as a table for version 2.</summary>
    private const int ParametricPoints = 4096;

    /// <summary>
    /// A profile of version 4 that is made of tone curves and a matrix (a grey tone curve, or three curves with the XYZ of the three colorants;
    /// ICC.1:2010, F.2/F.3) written as version 2.1 with the same colours: the header as it is (version 2.1, no profile ID), XYZ values, curves,
    /// the chromatic adaptation as they are, a curve given as a formula as a table of 4 096 points, the description and copyright as text of
    /// version 2. Null for any other profile (a table-based one, another version than 4, damaged).
    /// </summary>
    /// <param name="data">The profile.</param>
    /// <param name="language">The language whose record of a multilingual description or copyright is written (<see cref="MlucRecord"/>);
    /// null = the program language.</param>
    /// <returns>The profile of version 2.1; null if it cannot be written so.</returns>
    public static byte[]? AsVersion2(byte[] data, string? language = null)
    {
        if (data.Length < 132 || data[8] != 4 || BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20)) != 0x58595A20) return null;   // PCS XYZ
        var space = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(128));
        if (count > 100 || 132 + count * 12 > data.Length) return null;
        // only the tags written below, the same bytes copied once and never more than the profile holds — before, every one of up to 100
        // entries was copied, also duplicates and overlapping ones: a crafted profile of 32 MB took about 3.2 GB (docs/FEHLERGEDAECHTNIS.md,
        // No. 1122)
        HashSet<string> used = ["desc", "cprt", "wtpt", "bkpt", "rXYZ", "gXYZ", "bXYZ", "kTRC", "rTRC", "gTRC", "bTRC", "chad"];
        var read = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var copies = new Dictionary<(uint, uint), byte[]>();
        long copied = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = data.AsSpan(132 + i * 12, 12);
            var signature = Encoding.ASCII.GetString(entry[..4]);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var size = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            if (offset + (long)size > data.Length || size < 8) return null;
            if (!used.Contains(signature) || read.ContainsKey(signature)) continue;
            if (!copies.TryGetValue((offset, size), out var bytes))
            {
                if ((copied += size) > data.Length) return null;
                copies[(offset, size)] = bytes = data.AsSpan((int)offset, (int)size).ToArray();
                bytes.AsSpan(4, 4).Clear();   // reserved in every type written here (ICC.1, 10.1; before, copied as they were — No. 1133)
            }
            read[signature] = bytes;
        }
        string[] needed = space == IccProfile.GraySpace ? ["kTRC"] : space == IccProfile.RgbSpace ? ["rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC"] : [];
        if (needed.Length == 0 || needed.Any(n => !read.ContainsKey(n))) return null;
        var tags = new List<(string Signature, byte[] Data)>
        {
            ("desc", Desc(Ascii(TextOf(read.GetValueOrDefault("desc"), language) ?? "Profile"))),
            ("cprt", Text(Ascii(TextOf(read.GetValueOrDefault("cprt"), language) ?? ""))),
        };
        foreach (var signature in (ReadOnlySpan<string>)["wtpt", "bkpt", "rXYZ", "gXYZ", "bXYZ"])
            if (read.TryGetValue(signature, out var xyz))
            {
                if (!xyz.AsSpan().StartsWith("XYZ "u8)) return null;
                tags.Add((signature, xyz));
            }
            // the media white point is required (ICC.1, 8.2): D50, which the display takes without it — before, it was left out (No. 1133)
            else if (signature == "wtpt") tags.Add(("wtpt", Xyz(IccProfile.WhiteX, IccProfile.WhiteY, IccProfile.WhiteZ)));
        foreach (var signature in (ReadOnlySpan<string>)["kTRC", "rTRC", "gTRC", "bTRC"])
            if (read.TryGetValue(signature, out var curve))
            {
                if (curve.AsSpan().StartsWith("curv"u8)) tags.Add((signature, curve));
                else if (Parametric(curve) is { } table) tags.Add((signature, table));
                else return null;
            }
        if (read.TryGetValue("chad", out var adaptation) && adaptation.AsSpan().StartsWith("sf32"u8)) tags.Add(("chad", adaptation));
        var header = data.AsSpan(0, 128).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 0x02100000);
        header.AsSpan(84, 16).Clear();   // the profile ID is a field of version 4
        header.AsSpan(100, 28).Clear();  // reserved in both versions (ICC.1, 7.2.19; before, copied as they were — No. 1133)
        // a date that is none (month or day 0, hours beyond 23 …) becomes the fixed one of the own profiles (ICC.1 4.2, 7.2.8; before, copied as
        // it was — the PDF/A-1 way could write a profile with month 0; docs/FEHLERGEDAECHTNIS.md, No. 1128)
        if (!ValidCreated(header)) WriteCreated(header);
        return Assemble(header, tags);
    }

    /// <summary>The fixed date of creation of the own profiles (ICC.1, 7.2.8: a valid dateTimeNumber in UTC; No. 1124), also of cleaned ones.</summary>
    internal static void WriteCreated(Span<byte> header)
    {
        ReadOnlySpan<ushort> created = [2026, 11, 24, 0, 0, 0];
        for (var i = 0; i < created.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(header[(24 + 2 * i)..], created[i]);
    }

    /// <summary>The tags every profile but a DeviceLink carries (ICC.1, 8.2): description, copyright, media white point.</summary>
    private static readonly string[] RequiredTags = ["desc", "cprt", "wtpt"];

    /// <summary>The signatures of the tag table; null if the table does not fit the data.</summary>
    private static HashSet<string>? TagSignatures(ReadOnlySpan<byte> data)
    {
        if (data.Length < 132) return null;
        var count = BinaryPrimitives.ReadUInt32BigEndian(data[128..]);
        if (count > 1000 || 132 + count * 12 > data.Length) return null;
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++) signatures.Add(Encoding.ASCII.GetString(data.Slice(132 + 12 * i, 4)));
        return signatures;
    }

    /// <summary>
    /// The tags ICC.1 8.2 requires that a profile lacks (desc, cprt, wtpt; none for a DeviceLink); empty where it has them or where its tag
    /// table cannot be read (that is reported as an unreadable profile).
    /// </summary>
    /// <param name="data">The profile.</param>
    internal static string[] MissingRequiredTags(ReadOnlySpan<byte> data)
    {
        if (data.Length < 132 || data.Slice(12, 4).SequenceEqual("link"u8) || TagSignatures(data) is not { } signatures) return [];
        return [.. RequiredTags.Where(t => !signatures.Contains(t))];
    }

    /// <summary>
    /// The profile with the tags ICC.1 8.2 requires added where they are missing, without changing a colour: the media white point D50 (the
    /// white the display takes without it, also for the absolute intent), a neutral description („ICC profile“) and copyright („No copyright
    /// information“, no rights made up), as the types of its version (2: textDescriptionType and textType; from 4 on multiLocalizedUnicodeType,
    /// the profile ID then computed afresh, ICC.1 7.2.18). The tag table grows by the new entries, the data behind it move by as much, the new
    /// tags follow at the end. Null where nothing is missing, or where the profile cannot be completed so (an entry pointing into the header or
    /// the table, data larger than the profile).
    /// </summary>
    /// <param name="data">The profile.</param>
    internal static byte[]? WithRequiredTags(byte[] data)
    {
        var missing = MissingRequiredTags(data);
        if (missing.Length == 0) return null;
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(128));
        var tableEnd = 132 + 12 * count;
        var version4 = data[8] >= 4;
        var shift = 12 * missing.Length;
        for (var i = 0; i < count; i++)
        {
            var offset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(136 + 12 * i));
            var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(140 + 12 * i));
            if (offset < tableEnd || offset + (long)size > data.Length) return null;
        }
        var added = missing.Select(sig => (Signature: sig, Data: sig switch
        {
            "desc" => version4 ? Mluc("ICC profile") : Desc("ICC profile"),
            "cprt" => version4 ? Mluc("No copyright information") : Text("No copyright information"),
            _ => Xyz(IccProfile.WhiteX, IccProfile.WhiteY, IccProfile.WhiteZ),
        })).ToList();
        var body = (data.Length - tableEnd + 3) & ~3;   // the old tag data, padded to four bytes
        var length = tableEnd + shift + body + added.Sum(a => (a.Data.Length + 3) & ~3);
        var result = new byte[length];
        data.AsSpan(0, 128).CopyTo(result);
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)length);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(128), (uint)(count + added.Count));
        for (var i = 0; i < count; i++)
        {
            data.AsSpan(132 + 12 * i, 12).CopyTo(result.AsSpan(132 + 12 * i));
            var offset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(136 + 12 * i));
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(136 + 12 * i), (uint)(offset + shift));
        }
        data.AsSpan(tableEnd).CopyTo(result.AsSpan(tableEnd + shift));
        var at = tableEnd + shift + body;
        for (var k = 0; k < added.Count; k++)
        {
            var entry = result.AsSpan(132 + 12 * (count + k), 12);
            Encoding.ASCII.GetBytes(added[k].Signature, entry);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)added[k].Data.Length);
            added[k].Data.CopyTo(result, at);
            at += (added[k].Data.Length + 3) & ~3;
        }
        result.AsSpan(84, 16).Clear();
        if (version4) IccProfile.ComputedId(result).CopyTo(result, 84);
        return result;
    }

    /// <summary>Whether bytes 24–35 of a header are a date and time (ICC.1, 4.2).</summary>
    private static bool ValidCreated(byte[] header)
    {
        int Field(int i) => BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(24 + 2 * i));
        var (year, month, day, hour, minute, second) = (Field(0), Field(1), Field(2), Field(3), Field(4), Field(5));
        return year >= 1 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) && hour <= 23 && minute <= 59 && second <= 59;
    }

    /// <summary>
    /// The text of a description or copyright tag: of a multiLocalizedUnicodeType the record of the language asked for (<see cref="MlucRecord"/>;
    /// before, always the first — No. 1133), textType, textDescriptionType.
    /// </summary>
    private static string? TextOf(byte[]? tag, string? language)
    {
        if (tag is null || tag.Length < 12) return null;
        switch (Encoding.ASCII.GetString(tag, 0, 4))
        {
            // length and offset unsigned: as int, a value from 2^31 on passed the check and the decoding threw — saving as PDF/A-1 failed
            // (docs/FEHLERGEDAECHTNIS.md, No. 1122); no record at all: no text
            case "mluc" when tag.Length >= 28 && BinaryPrimitives.ReadUInt32BigEndian(tag.AsSpan(8)) > 0:
                return MlucRecord(tag, language) is var (offset, length) ? Encoding.BigEndianUnicode.GetString(tag, offset, length).TrimEnd('\0') : null;
            case "text":
                return Encoding.Latin1.GetString(tag, 8, tag.Length - 8).TrimEnd('\0');
            case "desc":
                var count = BinaryPrimitives.ReadUInt32BigEndian(tag.AsSpan(8));
                return 12 + (long)count <= tag.Length ? Encoding.Latin1.GetString(tag, 12, (int)count).TrimEnd('\0') : null;
            default:
                return null;
        }
    }

    /// <summary>Text for a tag of version 2: ASCII, other characters as „?“, without control characters.</summary>
    private static string Ascii(string text) => new([.. text.Select(c => c is >= ' ' and < (char)127 ? c : '?')]);

    /// <summary>A parametricCurveType (ICC.1:2010, 10.18) as a curveType table; null if it is not one or damaged.</summary>
    private static byte[]? Parametric(byte[] tag)
    {
        if (tag.Length < 16 || !tag.AsSpan().StartsWith("para"u8)) return null;
        var type = BinaryPrimitives.ReadUInt16BigEndian(tag.AsSpan(8));
        int[] counts = [1, 3, 4, 5, 7];
        if (type >= counts.Length || tag.Length < 12 + counts[type] * 4) return null;
        var p = new double[7];
        for (var i = 0; i < counts[type]; i++) p[i] = BinaryPrimitives.ReadInt32BigEndian(tag.AsSpan(12 + i * 4)) / 65536.0;
        var (g, a, b, c, d, e, f) = (p[0], p[1], p[2], p[3], p[4], p[5], p[6]);
        double Value(double x) => type switch
        {
            0 => Math.Pow(x, g),
            1 => a != 0 && x >= -b / a ? Math.Pow(Math.Max(0, a * x + b), g) : 0,
            2 => a != 0 && x >= -b / a ? Math.Pow(Math.Max(0, a * x + b), g) + c : c,
            3 => x >= d ? Math.Pow(Math.Max(0, a * x + b), g) : c * x,
            _ => x >= d ? Math.Pow(Math.Max(0, a * x + b), g) + e : c * x + f,
        };
        var data = new byte[12 + ParametricPoints * 2];
        "curv"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), ParametricPoints);
        for (var i = 0; i < ParametricPoints; i++)
        {
            var y = Value(i / (double)(ParametricPoints - 1));
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12 + i * 2), (ushort)Math.Round(Math.Clamp(double.IsFinite(y) ? y : 0, 0, 1) * 65535));
        }
        return data;
    }

    private static float[] WhitePoint(PdfDictionary? dict) =>
        PdfFunction.Numbers(dict?.GetArray(PdfName.Get("WhitePoint"))) is { Length: 3 } wp && wp[1] > 0
            ? [(float)wp[0], (float)wp[1], (float)wp[2]]
            : [IccProfile.WhiteX, IccProfile.WhiteY, IccProfile.WhiteZ];

    /// <summary>
    /// An own display profile: description, media white (D50), copyright and the tags given. Version 2.1: the texts as textDescriptionType and
    /// textType. Version 4.3 (ICC.1:2010, 8.2, 9.2.15, 7.2.18): the texts as multiLocalizedUnicodeType (en/US), the chromatic adaptation from
    /// <paramref name="adoptedWhite"/> to D50 (Bradford, as the colorants were adapted) in chad where that white is not D50, the profile ID set.
    /// Both carry the flag „embedded“ (ICC.1, 7.2.11 bit 0): PDF-Knead writes its profiles only into files (PDF, PNG, TIFF, JPEG) —
    /// before, the flags were 0.
    /// </summary>
    private static byte[] Build(uint space, List<(string Signature, byte[] Data)> own, string description = Description, bool version4 = false,
        double[]? adoptedWhite = null)
    {
        List<(string Signature, byte[] Data)> tags =
        [
            ("desc", version4 ? Mluc(description) : Desc(description)),
            ("wtpt", Xyz(IccProfile.WhiteX, IccProfile.WhiteY, IccProfile.WhiteZ)),
            ("cprt", version4 ? Mluc(Copyright) : Text(Copyright)),
            .. own,
        ];
        double[] d50 = [IccProfile.WhiteX, IccProfile.WhiteY, IccProfile.WhiteZ];
        if (version4 && adoptedWhite is { } white && !white.SequenceEqual(d50)) tags.Add(("chad", Sf32(Bradford(white, d50))));
        var header = new byte[128];
        var h = header.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(h[8..], version4 ? 0x04300000u : 0x02100000u);
        "mntr"u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32BigEndian(h[16..], space);
        "XYZ "u8.CopyTo(h[20..]);
        "acsp"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32BigEndian(h[44..], 1);   // embedded in a file (No. 1133)
        // the date of creation (ICC.1, 7.2.8: a valid dateTimeNumber in UTC) — fixed, the same profile always has the same bytes; before,
        // all zero, month 0 and day 0 are no date (docs/FEHLERGEDAECHTNIS.md, No. 1124)
        WriteCreated(h);
        Xyz(IccProfile.WhiteX, IccProfile.WhiteY, IccProfile.WhiteZ).AsSpan(8, 12).CopyTo(h[68..]);   // illuminant of the PCS
        var profile = Assemble(header, tags);
        if (version4) IccProfile.ComputedId(profile).CopyTo(profile, 84);
        return profile;
    }

    /// <summary>multiLocalizedUnicodeType (ICC.1, 10.15) with one record en/US of size 12: the text in UTF-16BE without a final zero.</summary>
    private static byte[] Mluc(string text)
    {
        var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        var data = new byte[28 + utf16.Length];
        "mluc"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), 12);
        "enUS"u8.CopyTo(data.AsSpan(16));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), (uint)utf16.Length);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), 28);
        utf16.CopyTo(data, 28);
        return data;
    }

    /// <summary>s15Fixed16ArrayType (ICC.1, 10.22) of a 3 × 3 matrix, row by row (the layout of chad, 9.2.15).</summary>
    private static byte[] Sf32(double[,] m)
    {
        var data = new byte[8 + 36];
        "sf32"u8.CopyTo(data);
        for (var i = 0; i < 9; i++) BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8 + 4 * i), (int)Math.Round(m[i / 3, i % 3] * 65536.0));
        return data;
    }

    /// <summary>
    /// The record of a multiLocalizedUnicodeType to show (ICC.1, 10.15 sentences 15–16): the one of the language and region asked for, else
    /// the first of the same language, else the first record; null where the tag has no record or the chosen one lies outside the tag.
    /// </summary>
    /// <param name="tag">The tag data, from its type signature „mluc“ on.</param>
    /// <param name="language">The language asked for as „de“, „de-CH“ or „de_CH“; null = the program language (<see cref="Localization.Tr.Current"/>)
    /// with the region of the system culture where that culture has the same language.</param>
    /// <returns>Offset and length of the chosen text in the tag.</returns>
    internal static (int Offset, int Length)? MlucRecord(ReadOnlySpan<byte> tag, string? language = null)
    {
        if (tag.Length < 16 || !tag.StartsWith("mluc"u8)) return null;
        var count = BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
        var size = BinaryPrimitives.ReadUInt32BigEndian(tag[12..]);
        if (count == 0 || size < 12) return null;
        var (wanted, region) = Wanted(language);
        long chosen = -1, sameLanguage = -1;
        // records only as far as the tag reaches (the count is a uInt32; size and count in long, as for cleaning, No. 1122)
        for (long r = 0; r < count && 16 + r * size + 12 <= tag.Length; r++)
        {
            var record = tag[(int)(16 + r * size)..];
            var code = Encoding.ASCII.GetString(record[..2]);
            if (!code.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (region is not null && Encoding.ASCII.GetString(record.Slice(2, 2)).Equals(region, StringComparison.OrdinalIgnoreCase))
            {
                chosen = r;
                break;
            }
            if (sameLanguage < 0) sameLanguage = r;
        }
        if (chosen < 0) chosen = sameLanguage >= 0 ? sameLanguage : 0;
        if (16 + chosen * size + 12 > tag.Length) return null;
        var at = tag[(int)(16 + chosen * size)..];
        var length = BinaryPrimitives.ReadUInt32BigEndian(at[4..]);
        var offset = BinaryPrimitives.ReadUInt32BigEndian(at[8..]);
        return offset + (long)length <= tag.Length ? ((int)offset, (int)length) : null;
    }

    /// <summary>The language (two letters, lower case) and the region (or null) asked for.</summary>
    private static (string Language, string? Region) Wanted(string? language)
    {
        var parts = (language ?? Localization.Tr.Current.Code).Split('-', '_');
        var wanted = parts[0].ToLowerInvariant();
        if (parts.Length > 1) return (wanted, parts[^1]);
        if (language is not null) return (wanted, null);
        // the program language names no region: that of the system culture, where it speaks the same language (de-AT → AT)
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        return culture.TwoLetterISOLanguageName == wanted && culture.Name.Split('-') is [_, .., { Length: 2 } region] ? (wanted, region) : (wanted, null);
    }

    /// <summary>A profile of a header (its size set here) and tags, each tag at a multiple of four bytes.</summary>
    private static byte[] Assemble(byte[] header, List<(string Signature, byte[] Data)> tags)
    {
        var offset = 128 + 4 + tags.Count * 12;
        var offsets = new int[tags.Count];
        for (var i = 0; i < tags.Count; i++)
        {
            offsets[i] = offset;
            offset += (tags[i].Data.Length + 3) & ~3;
        }
        var profile = new byte[offset];
        var p = profile.AsSpan();
        header.CopyTo(p);
        BinaryPrimitives.WriteUInt32BigEndian(p, (uint)offset);
        BinaryPrimitives.WriteUInt32BigEndian(p[128..], (uint)tags.Count);
        for (var i = 0; i < tags.Count; i++)
        {
            var entry = p.Slice(132 + i * 12, 12);
            Encoding.ASCII.GetBytes(tags[i].Signature, entry);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)tags[i].Data.Length);
            tags[i].Data.CopyTo(p[offsets[i]..]);
        }
        return profile;
    }

    /// <summary>XYZType: three s15Fixed16 numbers.</summary>
    private static byte[] Xyz(float x, float y, float z)
    {
        var data = new byte[20];
        "XYZ "u8.CopyTo(data);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), (int)Math.Round(x * 65536.0));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12), (int)Math.Round(y * 65536.0));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), (int)Math.Round(z * 65536.0));
        return data;
    }

    /// <summary>curveType with one entry: a pure gamma (u8Fixed8Number).</summary>
    private static byte[] Curve(double gamma)
    {
        var data = new byte[14];
        "curv"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), (ushort)Math.Clamp(Math.Round(gamma * 256), 1, ushort.MaxValue));
        return data;
    }

    /// <summary>curveType with a table: the sRGB tone curve (encoded value → linear light, IEC 61966-2.1, 5.2).</summary>
    private static byte[] SrgbCurve()
    {
        var data = new byte[12 + SrgbPoints * 2];
        "curv"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), SrgbPoints);
        for (var i = 0; i < SrgbPoints; i++)
        {
            var v = i / (double)(SrgbPoints - 1);
            var linear = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12 + i * 2), (ushort)Math.Round(linear * 65535));
        }
        return data;
    }

    /// <summary>textDescriptionType (version 2): ASCII, empty Unicode and ScriptCode parts.</summary>
    private static byte[] Desc(string text)
    {
        var ascii = Encoding.ASCII.GetBytes(text + "\0");
        var data = new byte[12 + ascii.Length + 8 + 3 + 67];
        "desc"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)ascii.Length);
        ascii.CopyTo(data, 12);
        return data;
    }

    /// <summary>textType: ASCII with a terminating zero.</summary>
    private static byte[] Text(string text)
    {
        var ascii = Encoding.ASCII.GetBytes(text + "\0");
        var data = new byte[8 + ascii.Length];
        "text"u8.CopyTo(data);
        ascii.CopyTo(data, 8);
        return data;
    }
}
