namespace PdfKnead.Core.Pdf.Filters.Jpx;

/// <summary>
/// The extensions of ISO/IEC 15444-2 a JPX baseline reader needs for its codestream (M.9.2.3), which ISO 32000-2, 7.4.9 requires: the
/// array-based multiple component transformation (Annex J; marker segments MCT, MCC, MCO and CBD, A.3.6–A.3.9) — of it the irreversible
/// decorrelation (J.3.1.1.1), with the restrictions of M.9.2.3 — and the non-linearity point transformation (Annex K; NLT, A.3.10) in all three
/// forms. Other transformations (dependency, reversible decorrelation, wavelet-based) are refused: no baseline file needs them.
/// </summary>
internal static class JpxPart2
{
    /// <summary>Most multiplications one collection of one tile may take.</summary>
    private const long MaxWork = 2_000_000_000;

    /// <summary>Most bytes the intermediate components of one stage of one tile may take: the budget of the machine, at most 1 GB.</summary>
    private static long MaxStageBytes => PdfLimits.Budget(1L << 30);

    /// <summary>Rsiz: extended capabilities of ISO/IEC 15444-2 (the two most significant bits 10 or 11, Table A.2).</summary>
    public static bool Extended(int rsiz) => (rsiz & 0x8000) != 0;

    /// <summary>
    /// Rsiz: capabilities ISO/IEC 15444-2, Table A.2 calls required to decode (no useful image without them) and the decoder does not implement:
    /// variable DC offset, variable scalar quantisation, single sample overlap, arbitrary decomposition styles, arbitrary and whole sample
    /// symmetric transformation kernels, arbitrary shaped region of interest, precinct-dependent quantisation. The multiple component
    /// transformation (bit 8) is implemented; the capabilities only useful to decode (trellis coded quantisation, visual masking, non-linearity)
    /// may be passed over (docs/FEHLERGEDAECHTNIS.md, No. 967).
    /// </summary>
    public static bool Unsupported(int rsiz) => Extended(rsiz) && (rsiz & 0x0CF3) != 0;

    /// <summary>Rsiz: the multiple component transformation capability.</summary>
    public static bool MultipleComponent(int rsiz) => Extended(rsiz) && (rsiz & 0x0100) != 0;

    /// <summary>Rsiz: the non-linearity point transformation capability.</summary>
    public static bool NonLinear(int rsiz) => Extended(rsiz) && (rsiz & 0x0200) != 0;

    private static int U16(byte[] d, int i) => d[i] << 8 | d[i + 1];

    // ---- Marker segments: series of segments with the same index in the same header, joined in the order of Z (J.2)

    /// <summary>
    /// One MCT segment (A.3.7): appended to its series by Zmct. A segment too short for Zmct and Imct (and Ymct in the first) is passed over and
    /// counts as damaged (<see cref="JpxPart2Header.Damaged"/>), as MCO and NLT — before it went without a word (probe of 23.11.2026).
    /// </summary>
    public static void ReadMct(JpxPart2Header h, byte[] d, int s, int e)
    {
        if (e - s < 4)
        {
            h.Damaged = true;
            return;
        }
        var z = U16(d, s);
        var key = U16(d, s + 2) & 0x0FFF;   // index, array type and element type: the ten low bits and the element type
        var payload = z == 0 ? s + 6 : s + 4;
        if (payload > e)
        {
            h.Damaged = true;
            return;
        }
        if (z == 0) h.ArrayFollowing[key] = U16(d, s + 4);   // Ymct: how many further segments the series has
        if (!h.ArraySeries.TryGetValue(key, out var parts)) h.ArraySeries[key] = parts = [];
        parts.Add((z, d[payload..e]));
    }

    /// <summary>
    /// One MCC segment (A.3.8): appended to its series by Zmcc; the first carries Qmcc. A segment too short for Zmcc and Imcc (and Ymcc, Qmcc in
    /// the first) is passed over and counts as damaged, as MCO and NLT — before it went without a word (probe of 23.11.2026).
    /// </summary>
    public static void ReadMcc(JpxPart2Header h, byte[] d, int s, int e)
    {
        if (e - s < 3)
        {
            h.Damaged = true;
            return;
        }
        var z = U16(d, s);
        var index = d[s + 2];
        if (!h.CollectionSeries.TryGetValue(index, out var series)) h.CollectionSeries[index] = series = (0, []);
        var payload = s + 3;
        if (z == 0)
        {
            if (e - s < 7)
            {
                h.Damaged = true;
                return;
            }
            h.CollectionFollowing[index] = U16(d, s + 3);   // Ymcc: how many further segments the series has
            series.Count = U16(d, s + 5);
            payload = s + 7;
            h.CollectionSeries[index] = series;
        }
        series.Parts.Add((z, d[payload..e]));
    }

    /// <summary>
    /// The MCO segment (A.3.9): the MCC index of each stage. A segment whose length is not 3 + Nmco (Equation A-6) — without Nmco, with fewer
    /// indices than Nmco or with bytes beyond them — gives the stages it has, at most Nmco, as before, and the header counts as damaged
    /// (<see cref="JpxPart2Header.Damaged"/>), as with NLT; before a short segment went without a word.
    /// </summary>
    public static void ReadMco(JpxPart2Header h, byte[] d, int s, int e)
    {
        if (e - s < 1)
        {
            h.Damaged = true;
            return;
        }
        if (e - s - 1 != d[s]) h.Damaged = true;
        var n = Math.Min(d[s], e - s - 1);
        h.Order = [.. d.AsSpan(s + 1, n)];
    }

    /// <summary>
    /// An NLT segment (A.3.10) for one component or (65 535) all. A damaged segment or one with reserved values — too short, a reserved type,
    /// BDnlt above 38 bits, PTval outside 1 to 32, fewer than two table values — is passed over as before; values the reader can use although
    /// they are reserved — Cnlt 16 384 to 65 534 (Table A.42: no component), Npoints above 8 191 (Table A.46), Dmax not above Dmin (K-6) — are
    /// kept; either way the header counts as damaged (<see cref="JpxPart2Header.Damaged"/>; docs/FEHLERGEDAECHTNIS.md, No. 1049).
    /// </summary>
    public static void ReadNlt(JpxPart2Header h, byte[] d, int s, int e)
    {
        if (e - s < 4)
        {
            h.Damaged = true;
            return;
        }
        var component = U16(d, s);
        var depth = d[s + 2];
        var type = d[s + 3];
        var p = s + 4;
        double[] parameters = [];
        double[]? table = null;
        double dmin = 0, dmax = 1;
        if (component is >= 16384 and < 65535) h.Damaged = true;
        switch (type)
        {
            case 1 when e - p >= 15:
                // E, S, T, A, B: each an 8-bit integer and a 16-bit fraction of 65 535 steps (Table A.45; docs/FEHLERGEDAECHTNIS.md, No. 971)
                parameters = [.. Enumerable.Range(0, 5).Select(i => d[p + 3 * i] + U16(d, p + 3 * i + 1) / 65535.0)];
                break;
            case 2 when e - p >= 11:
            {
                var field = U16(d, p);
                var points = field + 1;
                dmin = (uint)(d[p + 2] << 24 | d[p + 3] << 16 | d[p + 4] << 8 | d[p + 5]) / 4294967295.0;
                dmax = (uint)(d[p + 6] << 24 | d[p + 7] << 16 | d[p + 8] << 8 | d[p + 9]) / 4294967295.0;
                var bits = d[p + 10];
                if (bits is < 1 or > 32 || points < 2)
                {
                    h.Damaged = true;
                    return;
                }
                if (field > 8191 || dmax <= dmin) h.Damaged = true;
                var size = bits <= 8 ? 1 : bits <= 16 ? 2 : 4;
                if (e - p - 11 < (long)points * size)
                {
                    h.Damaged = true;
                    return;
                }
                var max = Math.ScaleB(1.0, bits) - 1;
                table = new double[points];
                for (var i = 0; i < points; i++)
                {
                    var q = p + 11 + i * size;
                    ulong v = 0;
                    for (var b = 0; b < size; b++) v = v << 8 | d[q + b];
                    table[i] = v / max;
                }
                break;
            }
            case 0 or 3:
                break;
            default:
                h.Damaged = true;
                return;
        }
        // BDnlt above 38 bits is reserved (Table A.43): read as a damaged segment, as before a reserved type — up to 128 bits ran the shifts
        // of the decoder over without a word (probe of T.801, 23.11.2026, B17)
        if ((depth & 0x7F) + 1 > 38)
        {
            h.Damaged = true;
            return;
        }
        h.Nlt[component] = new JpxNonLinearity((depth & 0x7F) + 1, (depth & 0x80) != 0, type, parameters, table, dmin, dmax);
    }

    /// <summary>The CBD segment (A.3.6): bit depth and sign of the reconstructed components; null if damaged.</summary>
    public static JpxComponentDepth[]? ReadCbd(byte[] d, int s, int e)
    {
        if (e - s < 3) return null;
        var n = U16(d, s);
        var count = n & 0x7FFF;
        var one = (n & 0x8000) != 0;
        if (count is < 1 or > 16384 || e - s - 2 < (one ? 1 : count)) return null;
        var result = new JpxComponentDepth[count];
        for (var i = 0; i < count; i++)
        {
            var bd = d[s + 2 + (one ? 0 : i)];
            // a bit depth above 38 is reserved (Table A.31): the segment counts as damaged (probe of T.801, 23.11.2026, B17)
            if ((bd & 0x7F) + 1 > 38) return null;
            result[i] = new JpxComponentDepth((bd & 0x7F) + 1, (bd & 0x80) != 0);
        }
        return result;
    }

    // ---- Effective values of a tile: its own (first tile-part header) before those of the main header

    /// <summary>A transformation array by index and type (0 dependency, 1 decorrelation, 2 offset); null if there is none.</summary>
    private static double[]? Array(JpxPart2Header tile, JpxPart2Header main, int index, int type)
    {
        foreach (var h in (ReadOnlySpan<JpxPart2Header>)[tile, main])
            foreach (var (key, parts) in h.ArraySeries)
                if ((key & 0xFF) == index && (key >> 8 & 3) == type)
                    return Values(Complete(parts, h.ArrayFollowing.GetValueOrDefault(key, -1), "MCT"), key >> 10 & 3);
        return null;
    }

    /// <summary>
    /// A series of MCT or MCC segments as ISO/IEC 15444-2, A.3.7 sentence 29 and A.3.8 sentence 28 have it: the first segment (Z = 0) names how
    /// many follow (Y), and Z runs from 0 to Y without a gap and without a number twice. Before the parts were joined by Z whatever they were;
    /// a gap showed only where the array came out too short, a doubled part counted twice (docs/FEHLERGEDAECHTNIS.md, No. 984).
    /// </summary>
    private static List<(int Z, byte[] Data)> Complete(List<(int Z, byte[] Data)> parts, int following, string kind)
    {
        var numbers = parts.Select(p => p.Z).Order().ToList();
        if (following < 0 || numbers.Count != following + 1 || numbers.Where((z, i) => z != i).Any())
            throw new JpxException($"JPX: {kind} unvollständig");
        return parts;
    }

    private static double[] Values(List<(int Z, byte[] Data)> parts, int elementType)
    {
        var data = parts.OrderBy(p => p.Z).SelectMany(p => p.Data).ToArray();
        var size = elementType switch { 0 => 2, 1 => 4, 2 => 4, _ => 8 };
        var values = new double[data.Length / size];
        for (var i = 0; i < values.Length; i++)
        {
            var at = i * size;
            values[i] = elementType switch
            {
                0 => (short)U16(data, at),
                1 => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at)),
                2 => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(at)),
                _ => System.Buffers.Binary.BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(at)),
            };
        }
        return values;
    }

    /// <summary>The collections of an MCC index: the tile's before the main header's; null if neither has it.</summary>
    private static List<JpxCollection>? Collections(JpxPart2Header tile, JpxPart2Header main, int index, JpxDecodeState? state)
    {
        foreach (var h in (ReadOnlySpan<JpxPart2Header>)[tile, main])
            if (h.CollectionSeries.TryGetValue(index, out var series))
                return ParseCollections(series.Count, Complete(series.Parts, h.CollectionFollowing.GetValueOrDefault(index, -1), "MCC"), state);
        return null;
    }

    /// <summary>
    /// The component collections of an MCC series (A.3.8). Values outside their range are read as given and reported (<paramref name="state"/>;
    /// docs/FEHLERGEDAECHTNIS.md, No. 1049): a number of inputs or outputs of 0 or above 16 384 (Tables A.36 and A.37: 1 to 16 384), and an
    /// output index named twice in the collections of one MCC (J.2.3.3) — the later collection gives that component, as before.
    /// </summary>
    private static List<JpxCollection> ParseCollections(int count, List<(int Z, byte[] Data)> parts, JpxDecodeState? state)
    {
        var d = parts.OrderBy(p => p.Z).SelectMany(p => p.Data).ToArray();
        var list = new List<JpxCollection>();
        var p = 0;
        var damaged = false;
        int[] Indices(int n, bool wide)
        {
            var size = wide ? 2 : 1;
            if (n is 0 or > 16384) damaged = true;
            if (p + (long)n * size > d.Length) throw new JpxException("JPX: MCC unvollständig");
            var result = new int[n];
            for (var i = 0; i < n; i++) result[i] = wide ? U16(d, p + 2 * i) : d[p + i];
            p += n * size;
            return result;
        }
        var named = new HashSet<int>();
        for (var c = 0; c < count; c++)
        {
            if (p + 3 > d.Length) throw new JpxException("JPX: MCC unvollständig");
            var kind = d[p] & 3;
            var nIn = U16(d, p + 1);
            p += 3;
            var inputs = Indices(nIn & 0x7FFF, (nIn & 0x8000) != 0);
            if (p + 2 > d.Length) throw new JpxException("JPX: MCC unvollständig");
            var nOut = U16(d, p);
            p += 2;
            var outputs = Indices(nOut & 0x7FFF, (nOut & 0x8000) != 0);
            foreach (var o in outputs)
                if (!named.Add(o)) damaged = true;
            if (p + 3 > d.Length) throw new JpxException("JPX: MCC unvollständig");
            var t = d[p] << 16 | d[p + 1] << 8 | d[p + 2];
            p += 3;
            if (kind == 3) p += 4;   // Omcc of a wavelet-based collection
            list.Add(new JpxCollection(kind, inputs, outputs, t & 0xFF, t >> 8 & 0xFF, (t & 0x10000) != 0));
        }
        if (damaged && state is not null) state.Truncated = true;
        return list;
    }

    // ---- The inverse multiple component transformation (J.2) of one tile

    /// <summary>
    /// The reconstructed components of a tile from its spatially reconstructed ones (J.2.1): stage by stage after the MCO segment, each
    /// collection an irreversible decorrelation, W = T·C + o (J.3.1.1.1; without an array the identity, without offsets zero). Components no
    /// stage fills stay zero (null components, J.2.3.1). No MCO, or Nmco = 0: the components as they are.
    /// </summary>
    /// <param name="tile">The tile's own values (first tile-part header).</param>
    /// <param name="main">The values of the main header.</param>
    /// <param name="input">The spatially reconstructed components (all of the same size).</param>
    /// <param name="outputs">Number of reconstructed components (Ncbd).</param>
    /// <param name="state">The work budget of the whole image; null: only the limit of the tile. Over it, the image is incomplete and the
    /// stages left out.</param>
    /// <returns>The reconstructed components, before rounding.</returns>
    /// <exception cref="JpxException">A transformation that JPX baseline does not need (dependency, reversible, wavelet-based), or more work than
    /// a tile may take.</exception>
    public static float[][] Transform(JpxPart2Header tile, JpxPart2Header main, float[][] input, int outputs, JpxDecodeState? state = null) =>
        Run(tile, main, input, outputs, state);

    /// <summary>
    /// <see cref="Transform"/> in double precision, for components from the exact store of reversible tile-components with many bit-planes
    /// (<see cref="JpxTileComponent.Exact"/>): their integers above 2^24 reach the decorrelation unrounded, and it computes with the array
    /// values as they were given.
    /// </summary>
    public static double[][] TransformExact(JpxPart2Header tile, JpxPart2Header main, double[][] input, int outputs, JpxDecodeState? state = null) =>
        Run(tile, main, input, outputs, state);

    private static T[][] Run<T>(JpxPart2Header tile, JpxPart2Header main, T[][] input, int outputs, JpxDecodeState? state)
        where T : struct, System.Numerics.IFloatingPointIeee754<T>
    {
        var size = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        var order = tile.Order ?? main.Order;
        var samples = input.Length > 0 ? input[0].Length : 0;
        var current = input;
        // all stages and collections of the tile together: up to 255 stages (the same collection again and again) of up to 65 535 collections
        // each, every one within the limit alone, made trillions of multiplications from a header of some kilobytes (probe of T.801,
        // 23.11.2026, B16)
        long total = 0;
        // each MCC and MCT series put together and read once per tile: a stage repeating the same collection, or many collections naming the
        // same array, joined and converted the whole series again each time (probe of T.801, 23.11.2026)
        var collectionsOf = new Dictionary<int, List<JpxCollection>>();
        var arrays = new Dictionary<(int Index, int Type), double[]?>();
        double[]? ArrayOf(int index, int type) =>
            arrays.TryGetValue((index, type), out var a) ? a : arrays[(index, type)] = Array(tile, main, index, type);
        foreach (var stage in order ?? [])
        {
            if (state is { OverBudget: true }) break;
            if (!collectionsOf.TryGetValue(stage, out var collections))
                collectionsOf[stage] = collections = Collections(tile, main, stage, state) ?? throw new JpxException("JPX: MCC fehlt");
            var count = collections.Count == 0 ? 0 : collections.Max(c => c.Outputs.Length == 0 ? 0 : c.Outputs.Max() + 1);
            // crafted files: tens of thousands of output components of a large tile would take many gigabytes
            if ((long)count * samples * size > MaxStageBytes || (long)outputs * samples * size > MaxStageBytes)
                throw new JpxException("JPX: Komponenten-Transformation zu groß");
            var next = new T[Math.Max(count, 0)][];
            foreach (var c in collections)
            {
                if (c.Kind != 1 || c.Reversible) throw new JpxException("JPX: Komponenten-Transformation nicht unterstützt");
                int n = c.Inputs.Length, m = c.Outputs.Length;
                // crafted files: thousands of components in one collection would cost hours (m · n operations per sample)
                // a collection without inputs still fills m components (from its offsets), and outputs named twice make count small: every output
                // counts as at least one operation per sample (probe of T.801, 23.11.2026)
                var work = (long)Math.Max(1, m) * Math.Max(1, n) * Math.Max(1, samples);
                if (work > MaxWork || (total += work) > MaxWork) throw new JpxException("JPX: Komponenten-Transformation zu groß");
                state?.AddWork(work);
                var t = c.TransformIndex == 0 ? null : ArrayOf(c.TransformIndex, 1) ?? throw new JpxException("JPX: MCT fehlt");
                var o = c.OffsetIndex == 0 ? null : ArrayOf(c.OffsetIndex, 2) ?? throw new JpxException("JPX: MCT fehlt");
                if (t is not null && t.Length < (long)m * n || o is not null && o.Length < m) throw new JpxException("JPX: MCT zu kurz");
                var sources = new T[n][];
                for (var j = 0; j < n; j++)
                    sources[j] = c.Inputs[j] < current.Length && current[c.Inputs[j]] is { } given ? given : new T[samples];
                for (var i = 0; i < m; i++)
                {
                    var w = new T[samples];
                    for (var j = 0; j < n; j++)
                    {
                        var coefficient = t is null ? (i == j ? T.One : T.Zero) : T.CreateTruncating(t[i * n + j]);
                        if (T.IsZero(coefficient)) continue;
                        var src = sources[j];
                        for (var k = 0; k < samples; k++) w[k] += coefficient * src[k];
                    }
                    if (o is not null && o[i] != 0)
                    {
                        var offset = T.CreateTruncating(o[i]);
                        for (var k = 0; k < samples; k++) w[k] += offset;
                    }
                    next[c.Outputs[i]] = w;
                }
            }
            current = next;
        }
        var result = new T[outputs][];
        for (var i = 0; i < outputs; i++) result[i] = i < current.Length && current[i] is { } given ? given : new T[samples];
        return result;
    }

    // ---- The reverse non-linearity (Annex K) of one value

    /// <summary>The NLT of a reconstructed component: its own (tile, then main header), else the default for all; null: none.</summary>
    public static JpxNonLinearity? NonLinearityOf(JpxPart2Header tile, JpxPart2Header main, int component) =>
        (tile.Nlt.GetValueOrDefault(component) ?? tile.Nlt.GetValueOrDefault(65535) ?? main.Nlt.GetValueOrDefault(component) ?? main.Nlt.GetValueOrDefault(65535))
            is { Type: not 0 } nlt ? nlt : null;

    /// <summary>
    /// A decoded value of a non-linearity (in its BDnlt) in the depth of the output component. The output depth comes from the NLT of the main
    /// header (K.1.3); an NLT of a tile-part header with another depth — or one where the main header has none — gives values of its own depth,
    /// scaled here: before they were taken as values of the output depth (docs/FEHLERGEDAECHTNIS.md, No. 986). Unsigned values count from 0 to
    /// their maximum, signed ones from minus to plus their half range.
    /// </summary>
    public static double ToDepth(double z, int fromBits, bool fromSigned, int toBits, bool toSigned)
    {
        if (fromBits == toBits && fromSigned == toSigned) return z;
        var normal = fromSigned ? z / Math.Max(1, Math.ScaleB(1.0, fromBits - 1) - 1) : z / Math.Max(1, Math.ScaleB(1.0, fromBits) - 1);
        if (fromSigned != toSigned) normal = toSigned ? 2 * normal - 1 : (normal + 1) / 2;
        return toSigned ? normal * (Math.ScaleB(1.0, toBits - 1) - 1) : normal * (Math.ScaleB(1.0, toBits) - 1);
    }

    /// <summary>
    /// The decoded value Z of a reconstructed value y (K.2): gamma style (K.2.1.2, Equation K-2: y/S up to the threshold T, otherwise
    /// ((|y| + B) / A)^(1/E), odd for negative values — the example of the standard is the curve of ITU-R BT.709; before the threshold was S·T
    /// and A and B were swapped, docs/FEHLERGEDAECHTNIS.md, No. 971), LUT style (K.2.2, K-6 to K-8:
    /// clipped to Dmin … Dmax, linear between the table values), or binary complement to sign-magnitude (K.2.3).
    /// </summary>
    /// <param name="nlt">The transformation.</param>
    /// <param name="y">The reconstructed value (rounded integer of its bit depth).</param>
    /// <param name="bits">Its bit depth.</param>
    /// <param name="signed">Whether it is signed.</param>
    /// <returns>Z in the bit depth and sign of <paramref name="nlt"/>.</returns>
    public static double Apply(JpxNonLinearity nlt, double y, int bits, bool signed)
    {
        switch (nlt.Type)
        {
            case 1:
            {
                var (e, sl, th, a, b) = (nlt.Parameters[0], nlt.Parameters[1], nlt.Parameters[2], nlt.Parameters[3], nlt.Parameters[4]);
                // a signed component of one bit has no positive range (2^0 − 1 = 0): at least 1, as in ToDepth — 0/0 gave NaN, and Math.Sign
                // threw on it (found by fuzzing; docs/FEHLERGEDAECHTNIS.md, No. 994)
                var yn = y / Math.Max(1, signed ? Math.ScaleB(1.0, bits - 1) - 1 : Math.ScaleB(1.0, bits) - 1);
                if (!double.IsFinite(yn)) yn = 0;
                double z;
                if (Math.Abs(yn) <= th) z = sl == 0 ? 0 : yn / sl;
                else z = Math.Sign(yn) * Math.Pow(Math.Max(0, (Math.Abs(yn) + b) / (a == 0 ? 1 : a)), e == 0 ? 1 : 1 / e);
                return z * (nlt.Signed ? Math.ScaleB(1.0, nlt.Bits - 1) - 1 : Math.ScaleB(1.0, nlt.Bits) - 1);
            }
            case 2 when nlt.Table is { Length: >= 2 } table:
            {
                var yn = signed ? (y + Math.ScaleB(1.0, bits - 1)) / (Math.ScaleB(1.0, bits) - 1) : y / (Math.ScaleB(1.0, bits) - 1);
                var step = (nlt.Dmax - nlt.Dmin) / (table.Length - 1);
                double zn;
                // Dmax not above Dmin (reserved, reported when read): every value clips to Dmin, where K-6 puts T_0 — the value at the one point
                // the range has; before the result was 0 (docs/FEHLERGEDAECHTNIS.md, No. 1049)
                if (step <= 0) zn = table[0];
                else
                {
                    yn = Math.Clamp(yn, nlt.Dmin, nlt.Dmax);
                    var k = Math.Min(table.Length - 2, (int)Math.Floor((yn - nlt.Dmin) / step));
                    zn = table[k] + (table[k + 1] - table[k]) * (yn - (nlt.Dmin + k * step)) / step;
                }
                var full = Math.ScaleB(1.0, nlt.Bits) - 1;
                return nlt.Signed ? zn * full - Math.ScaleB(1.0, nlt.Bits - 1) : zn * full;
            }
            case 3:
                return y >= 0 ? y : Math.Min(-Math.ScaleB(1.0, bits - 1) - y - 1, -1);
            default:
                return y;
        }
    }
}

/// <summary>Bit depth and sign of a reconstructed component (CBD, A.3.6).</summary>
internal readonly record struct JpxComponentDepth(int Bits, bool Signed);

/// <summary>A component collection of an MCC segment (A.3.8): transform kind (Xmcc), input and output indices, arrays, reversibility.</summary>
internal sealed record JpxCollection(int Kind, int[] Inputs, int[] Outputs, int TransformIndex, int OffsetIndex, bool Reversible);

/// <summary>An NLT segment (A.3.10): bit depth and sign of the decoded component, type, parameters (gamma), table (LUT).</summary>
internal sealed record JpxNonLinearity(int Bits, bool Signed, int Type, double[] Parameters, double[]? Table, double Dmin, double Dmax);

/// <summary>The Part 2 marker segments of one header (main or first tile-part).</summary>
internal sealed class JpxPart2Header
{
    /// <summary>MCT series by index, array type and element type: the parts by Zmct.</summary>
    public readonly Dictionary<int, List<(int Z, byte[] Data)>> ArraySeries = [];

    /// <summary>MCC series by Imcc: Qmcc and the parts by Zmcc.</summary>
    public readonly Dictionary<int, (int Count, List<(int Z, byte[] Data)> Parts)> CollectionSeries = [];

    /// <summary>Ymct of each MCT series (from its first segment): how many segments follow it.</summary>
    public readonly Dictionary<int, int> ArrayFollowing = [];

    /// <summary>Ymcc of each MCC series (from its first segment): how many segments follow it.</summary>
    public readonly Dictionary<int, int> CollectionFollowing = [];

    /// <summary>MCO: the MCC index of each stage; null without the segment.</summary>
    public int[]? Order;

    /// <summary>NLT by component (65 535: all).</summary>
    public readonly Dictionary<int, JpxNonLinearity> Nlt = [];

    /// <summary>An NLT segment of this header was damaged or had reserved values (<see cref="JpxPart2.ReadNlt"/>), or its MCO segment did not have
    /// the length of Equation A-6 (<see cref="JpxPart2.ReadMco"/>): the image counts as damaged.</summary>
    public bool Damaged;
}
