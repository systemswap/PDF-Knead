using System.Globalization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Graphics;

/// <summary>
/// PDF functions (ISO 32000-2, 7.10): type 0 (sampled), 2 (exponential), 3 (stitching),
/// 4 (PostScript calculator). Used for separation colours (Separation/DeviceN) and shadings.
/// Inputs and outputs are clamped to /Domain and /Range.
/// </summary>
/// <remarks>Instances are immutable after creation and can be evaluated from several threads at once.</remarks>
public abstract class PdfFunction
{
    private readonly double[] _domain;
    private readonly double[]? _range;

    /// <summary>Creates a function with the given domain and range.</summary>
    /// <param name="domain">/Domain: two numbers per input.</param>
    /// <param name="range">/Range: two numbers per output; null = outputs are not clamped.</param>
    protected PdfFunction(double[] domain, double[]? range)
    {
        _domain = domain;
        _range = range;
    }

    /// <summary>Number of inputs (half the length of /Domain).</summary>
    public int Inputs => _domain.Length / 2;

    /// <summary>/Domain as read: two numbers per input.</summary>
    internal double[] DomainArray => _domain;

    /// <summary>Number of outputs.</summary>
    public abstract int Outputs { get; }

    /// <summary>Evaluates the function.</summary>
    /// <param name="input">The input values; missing ones count as 0, all are clamped to /Domain (NaN becomes 0).</param>
    /// <param name="output">Receives the results; must be at least <see cref="Outputs"/> long.</param>
    public void Evaluate(ReadOnlySpan<double> input, Span<double> output)
    {
        Span<double> clipped = stackalloc double[Math.Max(Inputs, 1)];
        for (var i = 0; i < Inputs; i++)
        {
            var v = i < input.Length ? input[i] : 0;
            clipped[i] = Bounded(v, _domain[2 * i], _domain[2 * i + 1]);
        }
        Compute(clipped[..Inputs], output);
        if (_range is not null)
            for (var i = 0; i < Outputs && 2 * i + 1 < _range.Length; i++)
                output[i] = Bounded(output[i], _range[2 * i], _range[2 * i + 1]);
    }

    /// <summary>Clamps to [a, b], also if given the wrong way round (crafted files; Math.Clamp would throw) and without NaN.</summary>
    private static double Bounded(double v, double a, double b)
    {
        if (double.IsNaN(v)) v = 0;
        if (!(a <= b)) (a, b) = (double.IsNaN(b) ? double.MinValue : b, double.IsNaN(a) ? double.MaxValue : a);
        return Math.Clamp(v, a, b);
    }

    /// <summary>Computes the outputs from inputs that are already clamped to /Domain.</summary>
    /// <param name="input">Exactly <see cref="Inputs"/> values.</param>
    /// <param name="output">Receives at least <see cref="Outputs"/> values; clamped to /Range afterwards.</param>
    protected abstract void Compute(ReadOnlySpan<double> input, Span<double> output);

    /// <summary>Creates a function from a dictionary or stream; for an array of functions see <see cref="CreateArray"/>.</summary>
    /// <param name="obj">The function dictionary or stream.</param>
    /// <param name="doc">The document (stream data and cache).</param>
    /// <param name="depth">Nesting depth of stitching functions; deeper than 8 gives null.</param>
    /// <returns>The function; null if it cannot be read or has more inputs or outputs than <see cref="PdfLimits.MaxComponents"/>.</returns>
    /// <remarks>Built only once per object (otherwise samples and programs would be decoded again on every use).</remarks>
    public static PdfFunction? Create(PdfObject obj, PdfDocument doc, int depth = 0) =>
        obj is PdfDictionary or PdfStream ? doc.CachedFunction(obj, () => CreateUncached(obj, doc, depth)) : CreateUncached(obj, doc, depth);

    private static PdfFunction? CreateUncached(PdfObject obj, PdfDocument doc, int depth)
    {
        if (depth > 8 || obj.AsDictionary() is not { } dict) return null;
        var domain = Numbers(dict.GetArray(PdfName.Get("Domain")));
        if (domain is null || domain.Length < 2) domain = [0, 1];
        var range = Numbers(dict.GetArray(PdfName.Get("Range")));
        try
        {
            PdfFunction? function = (int)(dict.GetInteger(PdfName.Get("FunctionType")) ?? -1) switch
            {
                0 when obj is PdfStream s && range is not null => SampledFunction.Create(dict, doc.GetStreamData(s), domain, range, doc.ReserveFunctionSamples),
                2 => new ExponentialFunction(dict, domain, range),
                3 => StitchingFunction.Create(dict, doc, domain, range, depth),
                4 when obj is PdfStream s && range is not null => PostScriptFunction.Create(doc.GetStreamData(s), domain, range),
                _ => null,
            };
            // buffers for inputs and outputs live on the stack, so do not accept absurd numbers of them at all (crafted files)
            return function is { Inputs: <= PdfLimits.MaxComponents, Outputs: <= PdfLimits.MaxComponents } ? function : null;
        }
        catch (PdfException)
        {
            return null;
        }
    }

    /// <summary>Creates a function, or combines an array of functions with one output each (allowed for shadings).</summary>
    /// <param name="obj">A function, or an array of functions.</param>
    /// <param name="doc">The document.</param>
    /// <returns>The function; null if none can be read or the array has more than <see cref="PdfLimits.MaxComponents"/> entries.
    /// Unreadable entries of an array are skipped.</returns>
    public static PdfFunction? CreateArray(PdfObject obj, PdfDocument doc)
    {
        if (obj is not PdfArray array) return Create(obj, doc);
        // one function per colour component: more than 32 means a crafted file (200,000 parts would run into a stackalloc
        // and overflow the stack)
        if (array.Count > PdfLimits.MaxComponents) return null;
        var parts = new List<PdfFunction>();
        for (var i = 0; i < array.Count; i++)
            if (Create(array.Get(i), doc) is { } f) parts.Add(f);
        return parts.Count == 0 ? null : new CombinedFunction(parts);
    }

    internal static double[]? Numbers(PdfArray? array)
    {
        if (array is null) return null;
        var result = new double[array.Count];
        for (var i = 0; i < array.Count; i++) result[i] = array.GetNumber(i) ?? 0;
        return result;
    }

    internal static double Interpolate(double x, double xmin, double xmax, double ymin, double ymax) =>
        xmax == xmin ? ymin : ymin + (x - xmin) * (ymax - ymin) / (xmax - xmin);
}

/// <summary>
/// Several functions with one output each, side by side (shading /Function as an array). Their inputs are those of the first part: a
/// function-based shading (type 1) with one function per colour component has two, and counted as one it was invalid and fell back to grey
/// (docs/FEHLERGEDAECHTNIS.md, No. 436).
/// </summary>
internal sealed class CombinedFunction(List<PdfFunction> parts) : PdfFunction(parts[0].DomainArray, null)
{
    public override int Outputs => parts.Count;

    protected override void Compute(ReadOnlySpan<double> input, Span<double> output)
    {
        Span<double> one = stackalloc double[PdfLimits.MaxComponents];
        for (var i = 0; i < parts.Count; i++)
        {
            parts[i].Evaluate(input, one);
            output[i] = one[0];
        }
    }
}

/// <summary>Type 2: C0 + x^N · (C1 − C0).</summary>
internal sealed class ExponentialFunction : PdfFunction
{
    private readonly double[] _c0;
    private readonly double[] _c1;
    private readonly double _n;

    public ExponentialFunction(PdfDictionary dict, double[] domain, double[]? range) : base(domain, range)
    {
        _c0 = Numbers(dict.GetArray(PdfName.Get("C0"))) ?? [0];
        _c1 = Numbers(dict.GetArray(PdfName.Get("C1"))) ?? [1];
        _n = dict.GetNumber(PdfName.N) ?? 1;
    }

    public override int Outputs => Math.Min(_c0.Length, _c1.Length);

    protected override void Compute(ReadOnlySpan<double> input, Span<double> output)
    {
        var x = Math.Pow(input[0], _n);
        if (!double.IsFinite(x)) x = 0;
        for (var i = 0; i < Outputs; i++) output[i] = _c0[i] + x * (_c1[i] - _c0[i]);
    }
}

/// <summary>Type 3: subfunctions for intervals of the input.</summary>
internal sealed class StitchingFunction : PdfFunction
{
    private readonly PdfFunction[] _functions;
    private readonly double[] _bounds;
    private readonly double[] _encode;
    private readonly double _d0, _d1;

    private StitchingFunction(PdfFunction[] functions, double[] bounds, double[] encode, double[] domain, double[]? range)
        : base(domain, range)
    {
        _functions = functions;
        _bounds = bounds;
        Outputs = functions.Max(f => f.Outputs);
        _encode = encode;
        _d0 = domain[0];
        _d1 = domain[1];
    }

    public static StitchingFunction? Create(PdfDictionary dict, PdfDocument doc, double[] domain, double[]? range, int depth)
    {
        var array = dict.GetArray(PdfName.Get("Functions"));
        if (array is null || array.Count == 0) return null;
        var functions = new PdfFunction[array.Count];
        for (var i = 0; i < array.Count; i++)
            functions[i] = Create(array.Get(i), doc, depth + 1) ?? throw new PdfException("Funktion");
        var bounds = Numbers(dict.GetArray(PdfName.Get("Bounds"))) ?? [];
        var encode = Numbers(dict.GetArray(PdfName.Get("Encode"))) ?? [];
        // one bound too many, the last at the end of the domain (as some generators write it): read as the common readers do — without it
        // the gradient of a page was left out (docs/FEHLERGEDAECHTNIS.md, No. 436)
        if (bounds.Length == functions.Length && domain.Length >= 2 && bounds[^1] >= domain[1]) bounds = bounds[..^1];
        if (bounds.Length != functions.Length - 1) return null;
        if (encode.Length < 2 * functions.Length)
            encode = Enumerable.Range(0, functions.Length).SelectMany(_ => new[] { 0.0, 1.0 }).ToArray();
        return new StitchingFunction(functions, bounds, encode, domain, range);
    }

    // largest output count of the parts: the caller's buffer must suffice for every part (taking that of the first part
    // would let a part with more outputs write past the end)
    public override int Outputs { get; }

    protected override void Compute(ReadOnlySpan<double> input, Span<double> output)
    {
        var x = input[0];
        var k = 0;
        while (k < _bounds.Length && x >= _bounds[k]) k++;
        // ISO 32000-2, 7.10.4: if the first bound equals Domain0, the first interval is closed on both sides — x = Domain0 belongs to the
        // first function (and is encoded as Encode0); ISO 32000-1 did not allow such a bound (docs/FEHLERGEDAECHTNIS.md, No. 777)
        if (k > 0 && x <= _d0 && _bounds[0] <= _d0) k = 0;
        var low = k == 0 ? _d0 : _bounds[k - 1];
        var high = k == _bounds.Length ? _d1 : _bounds[k];
        Span<double> one = [Interpolate(x, low, high, _encode[2 * k], _encode[2 * k + 1])];
        _functions[k].Evaluate(one, output);
    }
}

/// <summary>
/// Type 0: table of samples, interpolated linearly (one input) or multilinearly (several inputs; above 8 inputs over those between two samples,
/// at most 4 096 corners, beyond that linearly on the simplex of the cell); with /Order 3 always by cubic splines in every input with at least
/// 4 samples (ISO 32000-2, 7.10.2; decided on 17.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 778, 785, 908). Every evaluation takes
/// the same way whatever was computed before; its cost is fixed by the function and never exceeds the size of its table (4 samples per cubic
/// input, at most 2 per linear one, each input having at least as many; the table at most 2^24 samples). The time is bounded per drawn object
/// (<see cref="FunctionWork"/>: an image beyond the bound is not shown, with a notice) — never by a silent switch to linear.
/// </summary>
internal sealed class SampledFunction : PdfFunction
{
    /// <summary>Above 8 inputs: multilinear over at most this many inputs between two samples (2^12 = 4 096 corners); beyond, the simplex.</summary>
    private const int MaxCornerBits = 12;


    // samples one cubic evaluation takes at most: 4 per cubic input, 2 per linear one
    private readonly long _cubicCost;

    private readonly int[] _size;
    private readonly double[] _encode;
    private readonly double[] _decode;
    private readonly double[] _samples;   // normalised to 0..1
    private readonly int _outputs;
    private readonly double[] _domain;
    private readonly bool[]? _cubic;      // per input: cubic spline (/Order 3 and Size ≥ 4); null: linear in every input

    private SampledFunction(int[] size, double[] encode, double[] decode, double[] samples, int outputs, double[] domain, double[] range, bool[]? cubic)
        : base(domain, range)
    {
        _size = size;
        _encode = encode;
        _decode = decode;
        _samples = samples;
        _outputs = outputs;
        _domain = domain;
        _cubic = cubic;
        if (cubic is not null)
        {
            _cubicCost = 1;
            for (var i = 0; i < size.Length; i++) _cubicCost *= cubic[i] ? 4 : Math.Min(size[i], 2);
        }
    }

    /// <summary>
    /// The inputs interpolated by cubic splines for /Order 3: those with at least 4 samples (with fewer, 7.10.2 has Order 3 ignored), however many
    /// inputs (before, above 8 inputs or 4 096 samples per evaluation it went linear; No. 908); null if none.
    /// </summary>
    private static bool[]? CubicInputs(PdfDictionary dict, int[] size)
    {
        if (dict.GetInteger(PdfName.Get("Order")) != 3) return null;
        var cubic = size.Select(s => s >= 4).ToArray();
        return cubic.Any(c => c) ? cubic : null;
    }

    /// <param name="reserve">Room for the samples within the document's budget (No. 756); null: no budget.</param>
    public static SampledFunction? Create(PdfDictionary dict, byte[] data, double[] domain, double[] range, Func<long, bool>? reserve = null)
    {
        var inputs = domain.Length / 2;
        var outputs = range.Length / 2;
        // a real where an integer belongs rounded, as every other integer entry (S-A A69, PdfDictionary.GetInteger) — before, /Size was cut off
        // and [2.6] took two samples where a reader that draws it takes three (docs/FEHLERGEDAECHTNIS.md, No. 905)
        var size = (Numbers(dict.GetArray(PdfName.Size)) ?? []).Select(v => Math.Max(1, (int)Math.Round(v))).ToArray();
        var bps = (int)(dict.GetInteger(PdfName.Get("BitsPerSample")) ?? 8);
        if (size.Length != inputs || outputs == 0 || bps is not (1 or 2 or 4 or 8 or 12 or 16 or 24 or 32)) return null;
        var encode = Numbers(dict.GetArray(PdfName.Get("Encode"))) ?? size.SelectMany(s => new[] { 0.0, s - 1.0 }).ToArray();
        var decode = Numbers(dict.GetArray(PdfName.Get("Decode"))) ?? range;

        if (inputs > PdfLimits.MaxComponents || outputs > PdfLimits.MaxComponents) return null;
        if (encode.Length < 2 * inputs) encode = size.SelectMany(s => new[] { 0.0, s - 1.0 }).ToArray();
        if (decode.Length < 2 * outputs) decode = range;
        // check the product of the sizes step by step (otherwise it overflows with several inputs and becomes small again)
        long count = outputs;
        foreach (var s in size)
            if ((count *= s) > 1 << 24) return null;
        if (reserve is not null && !reserve(count)) return null;
        var samples = new double[count];
        var max = Math.Pow(2, bps) - 1;
        long bit = 0;
        for (var i = 0; i < count; i++)
        {
            long value = 0;
            for (var b = 0; b < bps; b++, bit++)
            {
                var byteIndex = bit >> 3;
                var v = byteIndex < data.Length ? (data[byteIndex] >> (7 - (int)(bit & 7))) & 1 : 0;
                value = (value << 1) | (uint)v;
            }
            samples[i] = value / max;
        }
        return new SampledFunction(size, encode, decode, samples, outputs, domain, range, CubicInputs(dict, size));
    }

    public override int Outputs => _outputs;

    protected override void Compute(ReadOnlySpan<double> input, Span<double> output)
    {
        if (_cubic is not null)
        {
            FunctionWork.Charge(_cubicCost);
            Cubic(input, output);
            return;
        }
        if (_size.Length == 1)
        {
            var e = Math.Clamp(Interpolate(input[0], _domain[0], _domain[1], _encode[0], _encode[1]), 0, _size[0] - 1);
            var i0 = (int)Math.Floor(e);
            var i1 = Math.Min(i0 + 1, _size[0] - 1);
            var t = e - i0;
            for (var o = 0; o < _outputs; o++)
            {
                var v = _samples[i0 * _outputs + o] * (1 - t) + _samples[i1 * _outputs + o] * t;
                output[o] = Interpolate(v, 0, 1, _decode[2 * o], _decode[2 * o + 1]);
            }
            return;
        }

        // Several inputs: multilinear over the 2^n corners of the cell (the nearest sample gives blocks in function-based
        // shadings, and Math.Round(0.5) = 0 turns half colour coverage into white). Above 8 inputs: ManyInputs
        var n = _size.Length;
        Span<int> low = stackalloc int[n];
        Span<int> high = stackalloc int[n];
        Span<double> frac = stackalloc double[n];
        Span<int> strides = stackalloc int[n];
        var stride = 1;
        for (var i = 0; i < n; i++)
        {
            var e = Math.Clamp(Interpolate(input[i], _domain[2 * i], _domain[2 * i + 1], _encode[2 * i], _encode[2 * i + 1]), 0, _size[i] - 1);
            if (double.IsNaN(e)) e = 0;
            low[i] = (int)Math.Floor(e);
            high[i] = Math.Min(low[i] + 1, _size[i] - 1);
            frac[i] = e - low[i];
            strides[i] = stride;
            stride *= _size[i];
        }
        if (n > 8)
        {
            ManyInputs(n, low, high, frac, strides, output);
            return;
        }
        Span<double> sum = stackalloc double[_outputs];
        sum.Clear();
        for (var corner = 0; corner < 1 << n; corner++)
        {
            var weight = 1.0;
            var index = 0;
            for (var i = 0; i < n && weight > 0; i++)
            {
                var up = (corner >> i & 1) != 0;
                weight *= up ? frac[i] : 1 - frac[i];
                index += (up ? high[i] : low[i]) * strides[i];
            }
            if (weight <= 0) continue;
            for (var o = 0; o < _outputs; o++) sum[o] += weight * _samples[index * _outputs + o];
        }
        for (var o = 0; o < _outputs; o++)
            output[o] = Interpolate(sum[o], 0, 1, _decode[2 * o], _decode[2 * o + 1]);
    }

    /// <summary>
    /// More than 8 inputs (a DeviceN with many colourants; before, the nearest sample was taken — steps where the table is smooth): multilinear over
    /// the inputs that lie between two samples (one on a sample gives no branch), at most 2^<see cref="MaxCornerBits"/> corners; otherwise linearly
    /// on the simplex of the cell (Kuhn's division: the inputs sorted by their fraction, n + 1 corners) —
    /// through every sample as well and linear in between, in a time growing with n, not with 2^n (docs/FEHLERGEDAECHTNIS.md, No. 785).
    /// </summary>
    private void ManyInputs(int n, ReadOnlySpan<int> low, ReadOnlySpan<int> high, ReadOnlySpan<double> frac, ReadOnlySpan<int> strides, Span<double> output)
    {
        Span<int> branch = stackalloc int[n];
        var b = 0;
        var baseIndex = 0;
        for (var i = 0; i < n; i++)
        {
            baseIndex += low[i] * strides[i];
            if (frac[i] > 0 && high[i] != low[i]) branch[b++] = i;
        }
        Span<double> sum = stackalloc double[_outputs];
        sum.Clear();
        if (b <= MaxCornerBits)
        {
            FunctionWork.Charge(1L << b);
            for (var corner = 0; corner < 1 << b; corner++)
            {
                var weight = 1.0;
                var index = baseIndex;
                for (var j = 0; j < b; j++)
                {
                    var i = branch[j];
                    if ((corner >> j & 1) != 0)
                    {
                        weight *= frac[i];
                        index += (high[i] - low[i]) * strides[i];
                    }
                    else weight *= 1 - frac[i];
                }
                for (var o = 0; o < _outputs; o++) sum[o] += weight * _samples[index * _outputs + o];
            }
        }
        else
        {
            // the simplex: walk from the low corner along the inputs by falling fraction; corner k weighs f(k) − f(k+1), with f(0) = 1
            var order = branch[..b];
            for (var j = 1; j < b; j++)
                for (var k = j; k > 0 && frac[order[k]] > frac[order[k - 1]]; k--)
                    (order[k], order[k - 1]) = (order[k - 1], order[k]);
            var at = baseIndex;
            var previous = 1.0;
            for (var k = 0; k <= b; k++)
            {
                var f = k < b ? frac[order[k]] : 0;
                var weight = previous - f;
                for (var o = 0; o < _outputs; o++) sum[o] += weight * _samples[at * _outputs + o];
                if (k == b) break;
                at += (high[order[k]] - low[order[k]]) * strides[order[k]];
                previous = f;
            }
        }
        for (var o = 0; o < _outputs; o++)
            output[o] = Interpolate(sum[o], 0, 1, _decode[2 * o], _decode[2 * o + 1]);
    }

    /// <summary>
    /// /Order 3: cubic splines in the inputs marked in <see cref="_cubic"/> — Catmull-Rom with even knots: through every sample, with a continuous
    /// slope, each piece from the four samples around it, so a change in one place does not ripple through the table —, linear in the others,
    /// combined as a tensor product. At the ends of the table the spline goes on straight (a mirrored sample), so it neither flattens nor bends
    /// there, and a straight run of samples stays straight. Overshoot between samples is limited by the clipping to /Range. ISO 32000-2 names
    /// cubic spline interpolation without fixing the kind; widely used readers interpolate linearly (docs/FEHLERGEDAECHTNIS.md, No. 778).
    /// </summary>
    private void Cubic(ReadOnlySpan<double> input, Span<double> output)
    {
        var n = _size.Length;
        Span<int> count = stackalloc int[n];
        Span<int> index = stackalloc int[4 * n];
        Span<double> weight = stackalloc double[4 * n];
        Span<int> strides = stackalloc int[n];
        var stride = 1;
        for (var i = 0; i < n; i++)
        {
            strides[i] = stride;
            stride *= _size[i];
            var last = _size[i] - 1;
            var e = Math.Clamp(Interpolate(input[i], _domain[2 * i], _domain[2 * i + 1], _encode[2 * i], _encode[2 * i + 1]), 0, last);
            if (double.IsNaN(e)) e = 0;
            var at = 4 * i;
            if (last == 0)
            {
                (index[at], weight[at], count[i]) = (0, 1, 1);
                continue;
            }
            var k = Math.Min((int)Math.Floor(e), last - 1);   // the piece between samples k and k + 1
            var t = e - k;
            if (!_cubic![i])
            {
                (index[at], weight[at], index[at + 1], weight[at + 1], count[i]) = (k, 1 - t, k + 1, t, 2);
                continue;
            }
            double t2 = t * t, t3 = t2 * t;
            var w0 = 0.5 * (-t + 2 * t2 - t3);
            var w1 = 0.5 * (2 - 5 * t2 + 3 * t3);
            var w2 = 0.5 * (t + 4 * t2 - 3 * t3);
            var w3 = 0.5 * (t3 - t2);
            // a sample before the first or after the last is the mirror of its neighbour: p(−1) = 2 p(0) − p(1), p(n) = 2 p(n−1) − p(n−2)
            if (k == 0) (w1, w2, w0) = (w1 + 2 * w0, w2 - w0, 0);
            if (k + 1 == last) (w2, w1, w3) = (w2 + 2 * w3, w1 - w3, 0);
            var c = 0;
            if (w0 != 0) (index[at + c], weight[at + c++]) = (k - 1, w0);
            (index[at + c], weight[at + c++]) = (k, w1);
            (index[at + c], weight[at + c++]) = (k + 1, w2);
            if (w3 != 0) (index[at + c], weight[at + c++]) = (k + 2, w3);
            count[i] = c;
        }
        Span<double> sum = stackalloc double[_outputs];
        sum.Clear();
        Span<int> digit = stackalloc int[n];
        digit.Clear();
        while (true)
        {
            var w = 1.0;
            var sample = 0;
            for (var i = 0; i < n; i++)
            {
                w *= weight[4 * i + digit[i]];
                sample += index[4 * i + digit[i]] * strides[i];
            }
            if (w != 0)
                for (var o = 0; o < _outputs; o++) sum[o] += w * _samples[sample * _outputs + o];
            var d = 0;
            while (d < n && ++digit[d] == count[d]) digit[d++] = 0;
            if (d == n) break;
        }
        for (var o = 0; o < _outputs; o++)
            output[o] = Interpolate(sum[o], 0, 1, _decode[2 * o], _decode[2 * o + 1]);
    }
}

/// <summary>Type 4: small PostScript calculator (ISO 32000-2, 7.10.5) — arithmetic, comparisons, if/ifelse, stack operators.</summary>
internal sealed class PostScriptFunction : PdfFunction
{
    private enum Op : byte
    {
        Push, Abs, Add, Atan, Ceiling, Cos, Cvi, Cvr, Div, Exp, Floor, Idiv, Ln, Log, Mod, Mul, Neg, Round, Sin, Sqrt, Sub, Truncate,
        And, Bitshift, Eq, False, Ge, Gt, Le, Lt, Ne, Not, Or, True, Xor,
        Copy, Dup, Exch, Index, Pop, Roll,
        If, IfElse, Jump, JumpIfFalse,
    }

    // values an operator needs on the stack
    private static readonly byte[] Needs = BuildNeeds();

    private static byte[] BuildNeeds()
    {
        var needs = new byte[(int)Op.JumpIfFalse + 1];
        foreach (var op in new[] { Op.Abs, Op.Ceiling, Op.Cos, Op.Cvi, Op.Cvr, Op.Floor, Op.Ln, Op.Log, Op.Neg, Op.Round, Op.Sin, Op.Sqrt,
                     Op.Truncate, Op.Not, Op.Dup, Op.Pop, Op.Copy, Op.Index, Op.JumpIfFalse })
            needs[(int)op] = 1;
        foreach (var op in new[] { Op.Add, Op.Atan, Op.Div, Op.Exp, Op.Idiv, Op.Mod, Op.Mul, Op.Sub, Op.And, Op.Bitshift, Op.Eq, Op.Ge, Op.Gt,
                     Op.Le, Op.Lt, Op.Ne, Op.Or, Op.Xor, Op.Exch, Op.Roll })
            needs[(int)op] = 2;
        return needs;
    }

    private readonly Op[] _code;
    private readonly double[] _values;
    private readonly int _outputs;

    private PostScriptFunction(Op[] code, double[] values, int outputs, double[] domain, double[] range) : base(domain, range)
    {
        _code = code;
        _values = values;
        _outputs = outputs;
    }

    public override int Outputs => _outputs;

    public static PostScriptFunction? Create(byte[] program, double[] domain, double[] range)
    {
        var text = System.Text.Encoding.Latin1.GetString(program);
        var tokens = text.Replace("{", " { ").Replace("}", " } ").Split((char[])[' ', '\n', '\r', '\t', '\f', '\0'], StringSplitOptions.RemoveEmptyEntries);
        var pos = 0;
        if (tokens.Length == 0 || tokens[pos++] != "{") return null;
        var code = new List<Op>();
        var values = new List<double>();
        if (!Compile(tokens, ref pos, code, values, 0)) return null;
        return new PostScriptFunction([.. code], [.. values], range.Length / 2, domain, range);
    }

    /// <summary>
    /// Compiles up to the closing brace; "{…} if" and "{…} {…} ifelse" become jumps (forward only — no loops). Braces nest up to 255 deep,
    /// as deep as writers may (ISO 32000-2, 7.10.5.3); before, 32 refused valid functions (docs/FEHLERGEDAECHTNIS.md, No. 757).
    /// </summary>
    private static bool Compile(string[] tokens, ref int pos, List<Op> code, List<double> values, int depth)
    {
        if (depth > 256) return false;
        while (pos < tokens.Length)
        {
            var t = tokens[pos++];
            if (t == "}") return true;
            if (t == "{")
            {
                // block: compile as a conditional jump
                var jumpIfFalse = code.Count;
                code.Add(Op.JumpIfFalse);
                values.Add(0);
                if (!Compile(tokens, ref pos, code, values, depth + 1)) return false;
                if (pos < tokens.Length && tokens[pos] == "{")
                {
                    pos++;
                    var jumpOverElse = code.Count;
                    code.Add(Op.Jump);
                    values.Add(0);
                    values[jumpIfFalse] = code.Count;
                    if (!Compile(tokens, ref pos, code, values, depth + 1)) return false;
                    if (pos >= tokens.Length || tokens[pos++] != "ifelse") return false;
                    values[jumpOverElse] = code.Count;
                }
                else
                {
                    if (pos >= tokens.Length || tokens[pos++] != "if") return false;
                    values[jumpIfFalse] = code.Count;
                }
                continue;
            }
            // "NaN", "Infinity" and numbers beyond double are no PostScript numbers (an unknown name, an error): the function is refused
            // like one with an unknown operator (docs/FEHLERGEDAECHTNIS.md, No. 929)
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
            {
                code.Add(Op.Push);
                values.Add(number);
                continue;
            }
            if (!Enum.TryParse<Op>(t, ignoreCase: true, out var op) || op is Op.Push or Op.If or Op.IfElse or Op.Jump or Op.JumpIfFalse)
                return false;
            code.Add(op);
            values.Add(0);
        }
        return false;
    }

    /// <summary>
    /// Entries of the operand stack: at least 100 (ISO 32000-2, 7.10.5.3). A program goes on as long as no operator would put more on it;
    /// before, it stopped as soon as 98 entries lay on the stack, and a valid program with 98 to 100 entries gave wrong values
    /// (docs/FEHLERGEDAECHTNIS.md, No. 924).
    /// </summary>
    internal const int StackSize = 100;

    protected override void Compute(ReadOnlySpan<double> input, Span<double> output)
    {
        if (input.Length > StackSize)
        {
            output[.._outputs].Clear();
            return;
        }
        Span<double> stack = stackalloc double[StackSize];
        Span<double> tmp = stackalloc double[StackSize];
        var sp = 0;
        foreach (var v in input) stack[sp++] = v;

        try
        {
            Run(stack, tmp, ref sp);
        }
        catch (IndexOutOfRangeException)
        {
            // stack underflow in a broken program: result 0
            output[.._outputs].Clear();
            return;
        }

        // result: the topmost values
        for (var i = 0; i < _outputs; i++)
            output[i] = sp - _outputs + i >= 0 ? stack[sp - _outputs + i] : 0;
    }

    private void Run(Span<double> stack, Span<double> tmp, ref int sp)
    {
        for (var ip = 0; ip < _code.Length; ip++)
        {
            if (sp < 0) break;
            // overflow (an error by 7.10.5.3): stop instead of crashing — only where the operator puts one more entry on a full stack
            if (sp >= StackSize && _code[ip] is Op.Push or Op.True or Op.False or Op.Dup) break;
            if (sp < Needs[(int)_code[ip]]) return;   // underflow (crafted files): stop instead of running into an IndexOutOfRangeException
            double a, b;
            switch (_code[ip])
            {
                case Op.Push: stack[sp++] = _values[ip]; break;
                case Op.Abs: stack[sp - 1] = Math.Abs(stack[sp - 1]); break;
                case Op.Add: b = stack[--sp]; stack[sp - 1] += b; break;
                case Op.Atan:
                    b = stack[--sp]; a = stack[sp - 1];
                    var angle = Math.Atan2(a, b) * 180 / Math.PI;
                    stack[sp - 1] = angle < 0 ? angle + 360 : angle;
                    break;
                case Op.Ceiling: stack[sp - 1] = Math.Ceiling(stack[sp - 1]); break;
                case Op.Cos: stack[sp - 1] = Math.Cos(stack[sp - 1] * Math.PI / 180); break;
                case Op.Cvi: stack[sp - 1] = Math.Truncate(stack[sp - 1]); break;
                case Op.Cvr: break;
                case Op.Div: b = stack[--sp]; stack[sp - 1] = b == 0 ? 0 : stack[sp - 1] / b; break;
                case Op.Exp: b = stack[--sp]; stack[sp - 1] = Math.Pow(stack[sp - 1], b); break;
                case Op.Floor: stack[sp - 1] = Math.Floor(stack[sp - 1]); break;
                case Op.Idiv: b = stack[--sp]; stack[sp - 1] = b == 0 ? 0 : Math.Truncate(stack[sp - 1] / b); break;
                case Op.Ln: stack[sp - 1] = Math.Log(stack[sp - 1]); break;
                case Op.Log: stack[sp - 1] = Math.Log10(stack[sp - 1]); break;
                case Op.Mod: b = stack[--sp]; stack[sp - 1] = b == 0 ? 0 : stack[sp - 1] % b; break;
                case Op.Mul: b = stack[--sp]; stack[sp - 1] *= b; break;
                case Op.Neg: stack[sp - 1] = -stack[sp - 1]; break;
                case Op.Round: stack[sp - 1] = Math.Floor(stack[sp - 1] + 0.5); break;
                case Op.Sin: stack[sp - 1] = Math.Sin(stack[sp - 1] * Math.PI / 180); break;
                case Op.Sqrt: stack[sp - 1] = Math.Sqrt(stack[sp - 1]); break;
                case Op.Sub: b = stack[--sp]; stack[sp - 1] -= b; break;
                case Op.Truncate: stack[sp - 1] = Math.Truncate(stack[sp - 1]); break;
                case Op.And: b = stack[--sp]; stack[sp - 1] = (long)stack[sp - 1] & (long)b; break;
                case Op.Or: b = stack[--sp]; stack[sp - 1] = (long)stack[sp - 1] | (long)b; break;
                case Op.Xor: b = stack[--sp]; stack[sp - 1] = (long)stack[sp - 1] ^ (long)b; break;
                case Op.Not: stack[sp - 1] = stack[sp - 1] is 0 or 1 ? 1 - stack[sp - 1] : ~(long)stack[sp - 1]; break;
                case Op.Bitshift:
                    b = stack[--sp];
                    stack[sp - 1] = b >= 0 ? (long)stack[sp - 1] << (int)b : (long)stack[sp - 1] >> (int)-b;
                    break;
                case Op.Eq: b = stack[--sp]; stack[sp - 1] = stack[sp - 1] == b ? 1 : 0; break;
                case Op.Ne: b = stack[--sp]; stack[sp - 1] = stack[sp - 1] != b ? 1 : 0; break;
                case Op.Ge: b = stack[--sp]; stack[sp - 1] = stack[sp - 1] >= b ? 1 : 0; break;
                case Op.Gt: b = stack[--sp]; stack[sp - 1] = stack[sp - 1] > b ? 1 : 0; break;
                case Op.Le: b = stack[--sp]; stack[sp - 1] = stack[sp - 1] <= b ? 1 : 0; break;
                case Op.Lt: b = stack[--sp]; stack[sp - 1] = stack[sp - 1] < b ? 1 : 0; break;
                case Op.True: stack[sp++] = 1; break;
                case Op.False: stack[sp++] = 0; break;
                case Op.Dup: stack[sp] = stack[sp - 1]; sp++; break;
                case Op.Exch: (stack[sp - 1], stack[sp - 2]) = (stack[sp - 2], stack[sp - 1]); break;
                case Op.Pop: sp--; break;
                case Op.Copy:
                    var n = (int)stack[--sp];
                    if (n < 0 || n > sp || sp + n > StackSize) return;
                    for (var i = 0; i < n; i++) stack[sp + i] = stack[sp - n + i];
                    sp += n;
                    break;
                case Op.Index:
                    var k = (int)stack[sp - 1];
                    if (k < 0 || k >= sp - 1) return;
                    stack[sp - 1] = stack[sp - 2 - k];
                    break;
                case Op.Roll:
                    var j = (int)stack[--sp];
                    var count = (int)stack[--sp];
                    if (count <= 0 || count > sp) break;
                    j = ((j % count) + count) % count;
                    var slice = stack.Slice(sp - count, count);
                    for (var i = 0; i < count; i++) tmp[(i + j) % count] = slice[i];
                    tmp[..count].CopyTo(slice);
                    break;
                case Op.JumpIfFalse:
                    if (stack[--sp] == 0) ip = (int)_values[ip] - 1;
                    break;
                case Op.Jump:
                    ip = (int)_values[ip] - 1;
                    break;
            }
            if (sp < 0) return;
        }
    }
}
