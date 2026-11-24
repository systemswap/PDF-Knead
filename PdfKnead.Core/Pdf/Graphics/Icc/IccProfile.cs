using System.Buffers.Binary;

namespace PdfKnead.Core.Pdf.Graphics.Icc;

/// <summary>
/// ICC profile (ICC.1, versions 2 and 4), only the direction device → profile connection space (PCS, XYZ relative to media white, D50):
/// matrix with tone curves (grey, RGB) or tables (lut8, lut16, lutAtoB). Relative colorimetric (A2B1) is preferred to
/// perceptual (A2B0); <see cref="ForIntent"/> gives the tables of another rendering intent (A2B0 perceptual, A2B2 saturation; ISO 32000-2,
/// 8.6.5.8; docs/FEHLERGEDAECHTNIS.md, No. 572). For anything broken or unknown <see cref="Parse"/> returns null, and the alternate colour
/// space applies.
/// </summary>
internal sealed class IccProfile
{
    public const uint GraySpace = 0x47524159, RgbSpace = 0x52474220, CmykSpace = 0x434D594B, CmySpace = 0x434D5920, LabSpace = 0x4C616220;
    private const uint XyzSpace = 0x58595A20;

    /// <summary>White of the profile connection space (D50).</summary>
    public const float WhiteX = 0.9642f, WhiteY = 1f, WhiteZ = 0.8249f;

    /// <summary>Larger profiles are not read (real print profiles have a few MB).</summary>
    private const int MaxSize = 32 << 20;

    /// <summary>No table evaluated here has more inputs (2^n corners per value).</summary>
    private const int MaxInputs = 8;

    /// <summary>Points of a tone curve kept (curveType tables are resampled onto it; lut16 curves allow at most 4 096 as well).</summary>
    private const int MaxCurveEntries = 4096;

    private readonly IStage[] _stages;

    // the tables A2B0, A2B1, A2B2 as read (null where missing or unreadable), and the profiles built from them for another intent
    private IStage[]?[] _byIntent = [null, null, null];
    private readonly IccProfile?[] _variants = new IccProfile?[3];

    private IccProfile(byte[] data, uint space, int channels, IStage[] stages, bool matrixShaper)
    {
        Data = data;
        Space = space;
        Channels = channels;
        _stages = stages;
        IsMatrixShaper = matrixShaper;
    }

    /// <summary>
    /// The profile with the tables of a rendering intent (0 perceptual A2B0, 1 relative colorimetric A2B1, 2 saturation A2B2, 3 absolute
    /// colorimetric as relative): a missing table falls back as ICC.1 says (A2B1 and A2B2 to A2B0), then to the tables read by default. The
    /// same profile where nothing differs (matrix profiles, profiles with one table).
    /// </summary>
    /// <param name="intent">The rendering intent, 0 to 3.</param>
    /// <returns>This profile or a variant sharing its data.</returns>
    public IccProfile ForIntent(int intent)
    {
        var slot = intent switch { 0 => 0, 2 => 2, _ => 1 };
        var stages = slot switch
        {
            0 => _byIntent[0] ?? _stages,
            2 => _byIntent[2] ?? _byIntent[0] ?? _stages,
            _ => _stages,
        };
        if (ReferenceEquals(stages, _stages)) return this;
        lock (_variants)
            return _variants[slot] ??= new IccProfile(Data, Space, Channels, stages, matrixShaper: false);
    }

    public byte[] Data { get; }

    /// <summary>
    /// Entries of the colour tables read (all intents, as floats): what keeping the profile costs beyond its data — up to 4 bytes per byte of
    /// an 8-bit table (docs/FEHLERGEDAECHTNIS.md, No. 759).
    /// </summary>
    internal long TableEntries => _stages.Concat(_byIntent.Where(s => s is not null).SelectMany(s => s!)).Distinct()
        .Sum(s => s switch { ClutStage c => (long)c.Entries, CurveStage c => c.Entries, _ => 0L });
    public uint Space { get; }
    public int Channels { get; }

    /// <summary>Matrix and curves (not a table profile): <see cref="Matrix"/> and <see cref="Curves"/> are set.</summary>
    public bool IsMatrixShaper { get; }

    internal float[]? Matrix { get; private init; }
    internal IccCurve[]? Curves { get; private init; }

    /// <summary>
    /// The fingerprint of a profile: the profile ID of its header (bytes 84–99) where one is written, else the MD5 of the whole profile — a
    /// profile without an ID and its copy with the ID written count as the same (the ID is the MD5 with flags, rendering intent and ID zeroed,
    /// which for zero flags and intent is the MD5 of the copy without ID), a copy whose ID was changed as another (the test files of the reference
    /// validator for ISO 19005-4, 6.2.4.2 test 3, pass d and fail e; docs/FEHLERGEDAECHTNIS.md, No. 517). Null for data shorter than 128 bytes.
    /// </summary>
    public static byte[]? Fingerprint(ReadOnlySpan<byte> data)
    {
        if (data.Length < 128) return null;
        var id = data.Slice(84, 16);
        return id.IndexOfAnyExcept((byte)0) >= 0 ? id.ToArray() : System.Security.Cryptography.MD5.HashData(data);
    }

    /// <summary>The same profile: the same bytes, or the same <see cref="Fingerprint"/> (No. 517).</summary>
    public static bool SameProfile(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        a.SequenceEqual(b) || Fingerprint(a) is { } x && Fingerprint(b) is { } y && x.AsSpan().SequenceEqual(y);

    /// <summary>Reads a profile; null if it is broken, too large or cannot be evaluated.</summary>
    public static IccProfile? Parse(byte[] data)
    {
        try
        {
            return ParseCore(data);
        }
        catch (Exception e) when (e is IccFormatException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Device values 0…1 → XYZ (D50, relative to media white).</summary>
    public void ToXyz(ReadOnlySpan<float> device, Span<float> xyz)
    {
        Span<float> v = stackalloc float[16];
        for (var i = 0; i < Channels; i++) v[i] = i < device.Length ? Math.Clamp(device[i], 0, 1) : 0;
        foreach (var stage in _stages) stage.Apply(v);
        xyz[0] = v[0];
        xyz[1] = v[1];
        xyz[2] = v[2];
    }

    // ------------------------------------------------------------ Reading

    private sealed class IccFormatException() : Exception("ICC");

    private static uint U32(ReadOnlySpan<byte> d, int at) => BinaryPrimitives.ReadUInt32BigEndian(d[at..]);
    private static ushort U16(ReadOnlySpan<byte> d, int at) => BinaryPrimitives.ReadUInt16BigEndian(d[at..]);
    private static float S15(ReadOnlySpan<byte> d, int at) => BinaryPrimitives.ReadInt32BigEndian(d[at..]) / 65536f;

    private static IccProfile ParseCore(byte[] data)
    {
        if (data.Length < 132 || data.Length > MaxSize) throw new IccFormatException();
        ReadOnlySpan<byte> d = data;
        // use the declared size only if it fits; some producers write 0
        var size = (int)Math.Min(data.Length, U32(d, 0) is var s and > 0 ? s : (uint)data.Length);
        if (size < 132) size = data.Length;
        d = d[..size];
        if (U32(d, 36) != 0x61637370) throw new IccFormatException();   // 'acsp'
        var space = U32(d, 16);
        var pcs = U32(d, 20);
        if (pcs is not (XyzSpace or LabSpace)) throw new IccFormatException();
        var channels = ChannelsOf(space);
        if (channels == 0) throw new IccFormatException();

        var tags = new Dictionary<uint, (int Offset, int Size)>();
        var count = U32(d, 128);
        if (count > 1000) throw new IccFormatException();
        for (var i = 0; i < count; i++)
        {
            var at = 132 + 12 * i;
            if (at + 12 > d.Length) break;
            var offset = U32(d, at + 4);
            var length = U32(d, at + 8);
            if (offset >= d.Length || length > d.Length - offset) continue;
            tags.TryAdd(U32(d, at), ((int)offset, (int)length));
        }

        // tables: relative colorimetric, otherwise perceptual by default; all three kept for the intents
        IStage[]?[] byIntent = [null, null, null];
        uint[] tableTags = [0x41324230, 0x41324231, 0x41324232];   // A2B0, A2B1, A2B2
        // tags naming the same bytes (common, also in the bundled CMYK profile) share one reading — before, a table of 32 MB was built three
        // times, before any budget was asked (docs/FEHLERGEDAECHTNIS.md, No. 1122)
        var read = new Dictionary<(int, int), IStage[]?>();
        foreach (var i in (ReadOnlySpan<int>)[1, 0, 2])   // A2B1 first: as before, a broken A2B1 makes the profile unreadable
        {
            if (!tags.TryGetValue(tableTags[i], out var t)) continue;
            if (read.TryGetValue(t, out var same))
            {
                byIntent[i] = same;
                continue;
            }
            var lut = d.Slice(t.Offset, t.Size);
            if (lut.Length < 12) continue;
            try
            {
                byIntent[i] = U32(lut, 0) switch
                {
                    0x6D667431 => Lut8(lut, channels, pcs),        // mft1
                    0x6D667432 => Lut16(lut, channels, pcs),       // mft2
                    0x6D414220 => LutAToB(lut, channels, pcs),     // mAB
                    _ => null,
                };
                read[t] = byIntent[i];
            }
            catch (Exception e) when (i != 1 && e is IccFormatException or IndexOutOfRangeException or ArgumentException or OverflowException)
            {
                // a broken table of another intent: that intent falls back (before, only A2B1 and A2B0 were read, in this order)
                if (i == 0 && byIntent[1] is null) throw;
            }
        }
        if ((byIntent[1] ?? byIntent[0]) is { } chosen)
            return new IccProfile(data, space, channels, chosen, matrixShaper: false) { _byIntent = byIntent };

        // matrix and curves
        if (channels == 3 && tags.ContainsKey(0x7258595A))   // rXYZ
        {
            var matrix = new float[9];
            var columns = new uint[] { 0x7258595A, 0x6758595A, 0x6258595A };   // rXYZ, gXYZ, bXYZ
            for (var c = 0; c < 3; c++)
            {
                var (offset, length) = tags.TryGetValue(columns[c], out var x) ? x : throw new IccFormatException();
                if (length < 20 || U32(d, offset) != 0x58595A20) throw new IccFormatException();
                for (var r = 0; r < 3; r++) matrix[r * 3 + c] = S15(d, offset + 8 + 4 * r);
            }
            var curves = new IccCurve[3];
            var trcs = new uint[] { 0x72545243, 0x67545243, 0x62545243 };   // rTRC, gTRC, bTRC
            for (var c = 0; c < 3; c++)
            {
                var tc = tags.TryGetValue(trcs[c], out var found) ? found : throw new IccFormatException();
                // the same bytes once (No. 1122)
                curves[c] = c > 0 && tags[trcs[c - 1]] == tc ? curves[c - 1] : c > 1 && tags[trcs[0]] == tc ? curves[0] : Curve(d.Slice(tc.Offset, tc.Size), out _);
            }
            return new IccProfile(data, space, 3, [new CurveStage(curves), new MatrixStage(matrix, null)], matrixShaper: true)
            {
                Matrix = matrix,
                Curves = curves,
            };
        }
        if (channels == 1 && tags.TryGetValue(0x6B545243, out var k))   // kTRC
            return new IccProfile(data, space, 1, [new CurveStage([Curve(d.Slice(k.Offset, k.Size), out _)]), new GrayStage()], matrixShaper: false);
        throw new IccFormatException();
    }

    private static int ChannelsOf(uint space)
    {
        switch (space)
        {
            case GraySpace: return 1;
            case RgbSpace or CmySpace or LabSpace or XyzSpace: return 3;
            case 0x59436272 or 0x59787920 or 0x48535620 or 0x484C5320 or 0x4C757620: return 3;   // YCbr, Yxy, HSV, HLS, Luv
            case CmykSpace: return 4;
        }
        // 2CLR … FCLR: n colours
        if ((space & 0x00FFFFFF) == 0x00434C52)
        {
            var n = (int)(space >> 24);
            n = n is >= '2' and <= '9' ? n - '0' : n is >= 'A' and <= 'F' ? n - 'A' + 10 : 0;
            return n;
        }
        return 0;
    }

    /// <summary>Curve from 'curv' or 'para'; <paramref name="length"/> = bytes used (for sequences of curves in mAB).</summary>
    private static IccCurve Curve(ReadOnlySpan<byte> d, out int length)
    {
        if (d.Length < 12) throw new IccFormatException();
        var type = U32(d, 0);
        if (type == 0x63757276)   // curv
        {
            var count = (int)U32(d, 8);
            if (count < 0 || 12 + 2L * count > d.Length) throw new IccFormatException();
            length = 12 + 2 * count;
            if (count == 0) return IccCurve.Identity;
            if (count == 1) return IccCurve.Gamma(U16(d, 12) / 256f);
            // a table of up to 16.7 million entries fits into a profile of 32 MB, and several tags may name it: more than MaxCurveEntries
            // points are resampled linearly onto MaxCurveEntries — finer than any 16-bit evaluation needs (docs/FEHLERGEDAECHTNIS.md, No. 1122)
            var table = new float[Math.Min(count, MaxCurveEntries)];
            if (table.Length == count)
                for (var i = 0; i < count; i++) table[i] = U16(d, 12 + 2 * i) / 65535f;
            else
                for (var i = 0; i < table.Length; i++)
                {
                    var x = (double)i * (count - 1) / (table.Length - 1);
                    var k = Math.Min((int)x, count - 2);
                    var f = x - k;
                    table[i] = (float)((U16(d, 12 + 2 * k) * (1 - f) + U16(d, 14 + 2 * k) * f) / 65535);
                }
            return IccCurve.Table(table);
        }
        if (type == 0x70617261)   // para
        {
            var function = U16(d, 8);
            var needed = function switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => throw new IccFormatException() };
            if (12 + 4 * needed > d.Length) throw new IccFormatException();
            var p = new float[7];
            for (var i = 0; i < needed; i++) p[i] = S15(d, 12 + 4 * i);
            length = 12 + 4 * needed;
            return IccCurve.Parametric(function, p);
        }
        throw new IccFormatException();
    }

    // lut8 and lut16 (ICC.1:2010, lut8Type and lut16Type): input curves → table → output curves; the matrix applies only to XYZ input
    private static IStage[] Lut8(ReadOnlySpan<byte> d, int channels, uint pcs)
    {
        int inputs = d[8], outputs = d[9], grid = d[10];
        if (inputs != channels || outputs != 3 || inputs > MaxInputs || grid < 2) throw new IccFormatException();
        var at = 48;
        var inCurves = new IccCurve[inputs];
        for (var i = 0; i < inputs; i++, at += 256) inCurves[i] = IccCurve.Table(Bytes8(d, at, 256));
        var size = Points(grid, inputs) * outputs;
        var table = Bytes8(d, at, size);
        at += size;
        var outCurves = new IccCurve[outputs];
        for (var i = 0; i < outputs; i++, at += 256) outCurves[i] = IccCurve.Table(Bytes8(d, at, 256));
        return [new CurveStage(inCurves), new ClutStage(inputs, outputs, Enumerable.Repeat(grid, inputs).ToArray(), table), new CurveStage(outCurves),
            new PcsStage(pcs == LabSpace ? PcsEncoding.Lab8 : PcsEncoding.Xyz16)];
    }

    private static IStage[] Lut16(ReadOnlySpan<byte> d, int channels, uint pcs)
    {
        int inputs = d[8], outputs = d[9], grid = d[10];
        if (inputs != channels || outputs != 3 || inputs > MaxInputs || grid < 2 || d.Length < 52) throw new IccFormatException();
        int inEntries = U16(d, 48), outEntries = U16(d, 50);
        if (inEntries is < 2 or > 4096 || outEntries is < 2 or > 4096) throw new IccFormatException();
        var at = 52;
        var inCurves = new IccCurve[inputs];
        for (var i = 0; i < inputs; i++, at += 2 * inEntries) inCurves[i] = IccCurve.Table(Words16(d, at, inEntries));
        var size = Points(grid, inputs) * outputs;
        var table = Words16(d, at, size);
        at += 2 * size;
        var outCurves = new IccCurve[outputs];
        for (var i = 0; i < outputs; i++, at += 2 * outEntries) outCurves[i] = IccCurve.Table(Words16(d, at, outEntries));
        // lut16 always uses the legacy Lab encoding (L 100 = 0xFF00), also in version 4
        return [new CurveStage(inCurves), new ClutStage(inputs, outputs, Enumerable.Repeat(grid, inputs).ToArray(), table), new CurveStage(outCurves),
            new PcsStage(pcs == LabSpace ? PcsEncoding.LabLegacy16 : PcsEncoding.Xyz16)];
    }

    // lutAtoB (ICC.1:2010, lutAToBType): A curves → table → M curves → matrix → B curves; each element may be absent
    private static IStage[] LutAToB(ReadOnlySpan<byte> d, int channels, uint pcs)
    {
        int inputs = d[8], outputs = d[9];
        if (inputs != channels || outputs != 3 || inputs > MaxInputs || d.Length < 32) throw new IccFormatException();
        var bOffset = (int)U32(d, 12);
        var matrixOffset = (int)U32(d, 16);
        var mOffset = (int)U32(d, 20);
        var clutOffset = (int)U32(d, 24);
        var aOffset = (int)U32(d, 28);
        var stages = new List<IStage>();
        if (aOffset != 0) stages.Add(new CurveStage(CurveList(d, aOffset, inputs)));
        if (clutOffset != 0)
        {
            if (clutOffset + 20 > d.Length) throw new IccFormatException();
            var grid = new int[inputs];
            for (var i = 0; i < inputs; i++)
            {
                grid[i] = d[clutOffset + i];
                if (grid[i] < 2) throw new IccFormatException();
            }
            var precision = d[clutOffset + 16];
            var points = Points(grid);
            var size = points * outputs;
            var at = clutOffset + 20;
            var table = precision == 1 ? Bytes8(d, at, size) : precision == 2 ? Words16(d, at, size) : throw new IccFormatException();
            stages.Add(new ClutStage(inputs, outputs, grid, table));
        }
        else if (inputs != outputs)
        {
            throw new IccFormatException();
        }
        if (mOffset != 0) stages.Add(new CurveStage(CurveList(d, mOffset, outputs)));
        if (matrixOffset != 0)
        {
            if (matrixOffset + 48 > d.Length) throw new IccFormatException();
            var m = new float[9];
            var o = new float[3];
            for (var i = 0; i < 9; i++) m[i] = S15(d, matrixOffset + 4 * i);
            for (var i = 0; i < 3; i++) o[i] = S15(d, matrixOffset + 36 + 4 * i);
            stages.Add(new MatrixStage(m, o));
        }
        if (bOffset != 0) stages.Add(new CurveStage(CurveList(d, bOffset, outputs)));
        stages.Add(new PcsStage(pcs == LabSpace ? PcsEncoding.Lab16 : PcsEncoding.Xyz16));
        return [.. stages];
    }

    private static IccCurve[] CurveList(ReadOnlySpan<byte> d, int at, int count)
    {
        var curves = new IccCurve[count];
        for (var i = 0; i < count; i++)
        {
            if (at >= d.Length) throw new IccFormatException();
            curves[i] = Curve(d[at..], out var length);
            at += (length + 3) & ~3;   // each curve aligned to 4 bytes
        }
        return curves;
    }

    private static int Points(int grid, int inputs) => Points(Enumerable.Repeat(grid, inputs).ToArray());

    private static int Points(int[] grid)
    {
        long points = 1;
        foreach (var g in grid)
        {
            points *= g;
            if (points > 1 << 24) throw new IccFormatException();
        }
        return (int)points;
    }

    private static float[] Bytes8(ReadOnlySpan<byte> d, int at, int count)
    {
        if (at < 0 || at + (long)count > d.Length) throw new IccFormatException();
        var values = new float[count];
        for (var i = 0; i < count; i++) values[i] = d[at + i] / 255f;
        return values;
    }

    private static float[] Words16(ReadOnlySpan<byte> d, int at, int count)
    {
        if (at < 0 || at + 2L * count > d.Length) throw new IccFormatException();
        var values = new float[count];
        for (var i = 0; i < count; i++) values[i] = U16(d, at + 2 * i) / 65535f;
        return values;
    }

    // ------------------------------------------------------------ Stages

    private interface IStage
    {
        void Apply(Span<float> v);
    }

    private sealed class CurveStage(IccCurve[] curves) : IStage
    {
        /// <summary>Entries of the curves' tables, each table once.</summary>
        public long Entries => curves.Distinct().Sum(c => (long)c.Entries);

        public void Apply(Span<float> v)
        {
            for (var i = 0; i < curves.Length; i++) v[i] = curves[i].Evaluate(v[i]);
        }
    }

    /// <summary>3 × 3 (row by row) with an optional offset.</summary>
    private sealed class MatrixStage(float[] m, float[]? offset) : IStage
    {
        public void Apply(Span<float> v)
        {
            float a = v[0], b = v[1], c = v[2];
            v[0] = m[0] * a + m[1] * b + m[2] * c + (offset?[0] ?? 0);
            v[1] = m[3] * a + m[4] * b + m[5] * c + (offset?[1] ?? 0);
            v[2] = m[6] * a + m[7] * b + m[8] * c + (offset?[2] ?? 0);
        }
    }

    /// <summary>Grey: Y from the curve, chromaticity of the white point.</summary>
    private sealed class GrayStage : IStage
    {
        public void Apply(Span<float> v)
        {
            var y = v[0];
            v[0] = y * WhiteX;
            v[1] = y * WhiteY;
            v[2] = y * WhiteZ;
        }
    }

    /// <summary>Multidimensional table, n-linearly interpolated; the first input varies slowest.</summary>
    private sealed class ClutStage : IStage
    {
        private readonly int _inputs;
        private readonly int _outputs;
        private readonly int[] _grid;
        private readonly int[] _stride;
        private readonly float[] _table;

        /// <summary>Entries of the table.</summary>
        public int Entries => _table.Length;

        public ClutStage(int inputs, int outputs, int[] grid, float[] table)
        {
            _inputs = inputs;
            _outputs = outputs;
            _grid = grid;
            _table = table;
            _stride = new int[inputs];
            var s = outputs;
            for (var i = inputs - 1; i >= 0; i--)
            {
                _stride[i] = s;
                s *= grid[i];
            }
        }

        public void Apply(Span<float> v)
        {
            Span<int> index = stackalloc int[_inputs];
            Span<float> frac = stackalloc float[_inputs];
            for (var i = 0; i < _inputs; i++)
            {
                var x = Math.Clamp(v[i], 0, 1) * (_grid[i] - 1);
                var j = Math.Min((int)x, _grid[i] - 2);
                index[i] = j;
                frac[i] = x - j;
            }
            Span<float> sum = stackalloc float[_outputs];
            sum.Clear();
            for (var corner = 0; corner < 1 << _inputs; corner++)
            {
                var weight = 1f;
                var at = 0;
                for (var i = 0; i < _inputs; i++)
                {
                    var up = (corner >> (_inputs - 1 - i) & 1) != 0;
                    weight *= up ? frac[i] : 1 - frac[i];
                    at += (index[i] + (up ? 1 : 0)) * _stride[i];
                }
                if (weight == 0) continue;
                for (var o = 0; o < _outputs; o++) sum[o] += weight * _table[at + o];
            }
            for (var o = 0; o < _outputs; o++) v[o] = sum[o];
        }
    }

    private enum PcsEncoding
    {
        Lab8,
        LabLegacy16,
        Lab16,
        Xyz16,
    }

    /// <summary>Table output (0…1) → XYZ D50.</summary>
    private sealed class PcsStage(PcsEncoding encoding) : IStage
    {
        public void Apply(Span<float> v)
        {
            if (encoding == PcsEncoding.Xyz16)
            {
                // u1Fixed15: 1.0 = 0x8000
                const float scale = 65535f / 32768f;
                v[0] *= scale;
                v[1] *= scale;
                v[2] *= scale;
                return;
            }
            float l, a, b;
            switch (encoding)
            {
                case PcsEncoding.Lab8:
                    (l, a, b) = (v[0] * 100, v[1] * 255 - 128, v[2] * 255 - 128);
                    break;
                case PcsEncoding.LabLegacy16:
                    const float legacy = 65535f / 65280f;
                    (l, a, b) = (v[0] * legacy * 100, v[1] * legacy * 255 - 128, v[2] * legacy * 255 - 128);
                    break;
                default:
                    (l, a, b) = (v[0] * 100, v[1] * 255 - 128, v[2] * 255 - 128);
                    break;
            }
            IccColor.LabToXyz(l, a, b, v);
        }
    }
}

/// <summary>Tone curve: identity, power, table or parametric (ICC.1:2010, curveType and parametricCurveType). The input is clamped to 0–1, parametric results as well.</summary>
internal sealed class IccCurve
{
    private readonly int _kind;   // 0 identity, 1 power, 2 table, 3 parametric
    private readonly float _gamma;
    private readonly float[]? _table;
    private readonly int _function;
    private readonly float[]? _p;

    private IccCurve(int kind, float gamma = 1, float[]? table = null, int function = 0, float[]? p = null)
    {
        _kind = kind;
        _gamma = gamma;
        _table = table;
        _function = function;
        _p = p;
    }

    public static readonly IccCurve Identity = new(0);

    public static IccCurve Gamma(float gamma) => new(1, gamma);

    public static IccCurve Table(float[] table) => new(2, table: table);

    /// <summary>Entries of its table (0 for identity, power and parametric curves).</summary>
    public int Entries => _table?.Length ?? 0;

    public static IccCurve Parametric(int function, float[] p) => new(3, function: function, p: p);

    public float Evaluate(float x)
    {
        x = Math.Clamp(x, 0, 1);
        switch (_kind)
        {
            case 0:
                return x;
            case 1:
                return MathF.Pow(x, _gamma);
            case 2:
            {
                var t = _table!;
                var pos = x * (t.Length - 1);
                var i = Math.Min((int)pos, t.Length - 2);
                return t[i] + (t[i + 1] - t[i]) * (pos - i);
            }
            default:
            {
                var p = _p!;
                float g = p[0], a = p[1], b = p[2], c = p[3], d = p[4], e = p[5], f = p[6];
                float y = _function switch
                {
                    0 => MathF.Pow(x, g),
                    1 => x >= -b / a ? MathF.Pow(Math.Max(0, a * x + b), g) : 0,
                    2 => x >= -b / a ? MathF.Pow(Math.Max(0, a * x + b), g) + c : c,
                    3 => x >= d ? MathF.Pow(Math.Max(0, a * x + b), g) : c * x,
                    _ => x >= d ? MathF.Pow(Math.Max(0, a * x + b), g) + e : c * x + f,
                };
                return float.IsNaN(y) ? 0 : Math.Clamp(y, 0, 1);
            }
        }
    }
}
