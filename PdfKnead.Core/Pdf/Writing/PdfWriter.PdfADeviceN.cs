using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>
        /// A DeviceN space with more colourants than the part allows (ISO 19005-2, 6.1.13: 32; ISO 19005-1, 6.1.12: 8), as the display draws
        /// it: its values through the tint transform into the alternate space — or, with more colourants than the display reads
        /// (<see cref="PdfLimits.MaxComponents"/>), the first value as grey, as it draws such a space.
        /// </summary>
        /// <param name="Inputs">The number of colourants.</param>
        /// <param name="Alternate">The space the values become, as read.</param>
        /// <param name="From">The source of <paramref name="Alternate"/> and the tint transform.</param>
        /// <param name="Convert">The values in that space, as the display computes them.</param>
        /// <param name="TintRaw">The tint transform as read; null where the display does not use it.</param>
        /// <param name="Outputs">The number of values of that space.</param>
        private sealed record WideSpace(int Inputs, PdfObject Alternate, Source? From, Func<double[], double[]> Convert, PdfObject? TintRaw, int Outputs);

        private readonly Dictionary<PdfArray, WideSpace?> _wideSpaces = new(ReferenceEqualityComparer.Instance);

        /// <summary>The DeviceN space a value is, if it has more colourants than the part allows; null otherwise or if it cannot be read.</summary>
        private WideSpace? Wide(PdfObject? raw, Source? source)
        {
            if (raw is null || Look(raw, source) is not (PdfArray { Count: >= 4 } array, var from) || array.Get(0) is not PdfName { Value: "DeviceN" } ||
                from is null || PdfAResolve(array[1], from) is not PdfArray names || names.Count <= _pdfA!.Part.Limits().Colourants)
                return null;
            if (_wideSpaces.TryGetValue(array, out var known)) return known;
            WideSpace? wide = null;
            try
            {
                var alternate = ColorSpace.Create(array.Get(2), null, from.Doc);
                if (names.Count > PdfLimits.MaxComponents)
                    wide = new WideSpace(names.Count, PdfName.Get("DeviceGray"), from, values => [values.Length > 0 ? values[0] : 0], null, 1);
                else if (PdfFunction.Create(array.Get(3), from.Doc) is { } tint && alternate is { IsPattern: false, IsIndexed: false, IsColorant: false } &&
                         alternate.Components > 0)
                {
                    var outputs = alternate.Components;
                    wide = new WideSpace(names.Count, array[2], from, values =>
                    {
                        var output = new double[Math.Max(outputs, tint.Outputs)];
                        tint.Evaluate(values, output);
                        return output[..outputs];
                    }, array[3], outputs);
                }
            }
            catch (PdfException)
            {
            }
            return _wideSpaces[array] = wide;
        }

        /// <summary>The alternate space of a DeviceN space too wide for the part, written for an object of <paramref name="source"/>.</summary>
        private PdfObject WideAlternate(WideSpace wide, Source? source)
        {
            var resolved = PdfAResolve(wide.Alternate, wide.From) ?? PdfName.Get("DeviceRGB");
            return resolved is PdfArray alternateArray ? PdfAFix(Portable(alternateArray, wide.From, source), source, 0) : Portable(resolved, wide.From, source);
        }

        /// <summary>
        /// A colour space array that is or contains a DeviceN space too wide for the part, as the copy writes it: the alternate space in its
        /// place — within an indexed space with its colour table changed, within a pattern space as the underlying space (the content changes
        /// its values, <see cref="ColourChangeOf"/>). Null for any other array.
        /// </summary>
        private PdfObject? WithoutWideSpace(PdfArray array, Source? source)
        {
            if (Wide(array, source) is { } wide) return WideAlternate(wide, source);
            if (array.Count >= 2 && array.Get(0) is PdfName { Value: "Pattern" } && Wide(array[1], source) is { } under)
                return new PdfArray([PdfName.Get("Pattern"), WideAlternate(under, source)]);
            if (array.Count >= 4 && array.Get(0) is PdfName { Value: "Indexed" or "I" } && Wide(array[1], source) is { } bas &&
                array.Get(2).AsNumber() is { } hival && IndexTable(PdfAResolve(array[3], source), source) is { } table)
            {
                var entries = (int)Math.Clamp(hival, 0, 255) + 1;
                var changed = new byte[entries * bas.Outputs];
                var values = new double[bas.Inputs];
                for (var i = 0; i < entries; i++)
                {
                    for (var c = 0; c < bas.Inputs; c++) values[c] = i * bas.Inputs + c < table.Length ? table[i * bas.Inputs + c] / 255.0 : 0;
                    var converted = bas.Convert(values);
                    for (var c = 0; c < bas.Outputs; c++) changed[i * bas.Outputs + c] = (byte)Math.Clamp((int)Math.Round(converted[c] * 255), 0, 255);
                }
                return new PdfArray([PdfName.Get("Indexed"), WideAlternate(bas, source), PdfInteger.Of(entries - 1), new PdfString(changed, isHex: true)]);
            }
            return null;
        }

        /// <summary>The colour table of an indexed space (a string or a stream).</summary>
        private static byte[]? IndexTable(PdfObject? table, Source? source)
        {
            try
            {
                return table switch
                {
                    PdfString s => s.Bytes,
                    PdfStream stream when source is not null => source.Doc.DecodeStream(stream).Data,
                    _ => null,
                };
            }
            catch (PdfException)
            {
                return null;
            }
        }

        /// <summary>
        /// How the values of the colour space named <paramref name="name"/> in <paramref name="resources"/> (as read) change in the copy: a
        /// DeviceN space too wide for the part (directly or under a pattern) through its tint transform; null where they stay.
        /// </summary>
        private ColourChange? ColourChangeOf(PdfDictionary? resources, PdfDocument doc, PdfName name)
        {
            if (resources?.GetDictionary(PdfName.Get("ColorSpace"))?.GetRaw(name) is not { } raw || SourceOf(doc) is not { } source) return null;
            if (Wide(raw, source) is { } wide) return new ColourChange(wide.Inputs, wide.Convert);
            if (PdfAResolve(raw, source) is PdfArray { Count: >= 2 } pattern && pattern.Get(0) is PdfName { Value: "Pattern" } && Wide(pattern[1], source) is { } under)
                return new ColourChange(under.Inputs, under.Convert, Pattern: true);
            return null;
        }

        /// <summary>The source of a document being written.</summary>
        private Source? SourceOf(PdfDocument doc) => _sources.TryGetValue(doc, out var source) ? source : null;

        /// <summary>
        /// A shading in a DeviceN space too wide for the part (<paramref name="read"/> as read), as the copy writes it: its function followed by
        /// the tint transform — one PostScript calculator where both are such programs (the values clipped between them as the display clips
        /// them), otherwise the two sampled as the display evaluates them (4 096 points along one input, 128 × 128 for two); /Background
        /// changed alike. A mesh without a function keeps its DeviceN space (its colours are in its data) — and the check names it. Null for any
        /// other dictionary.
        /// </summary>
        private PdfDictionary? WithoutWideShading(PdfDictionary written, PdfDictionary read, Source? source)
        {
            if (!read.ContainsKey(PdfName.Get("ShadingType")) || read.GetRaw(PdfName.Get("ColorSpace")) is not { } space || Wide(space, source) is not { } wide ||
                source is null)
                return null;
            var result = CloneWithResolver(written);
            if (read.GetRaw(PdfName.Get("Function")) is not { } function)
            {
                if (PdfAResolve(space, source) is PdfArray deviceN) result.Set(PdfName.Get("ColorSpace"), Rebase(deviceN, source));
                return result;
            }
            if (Composed(function, wide, source, read.GetInteger(PdfName.Get("ShadingType")) == 1 ? 2 : 1) is not { } composed) return null;
            result.Set(PdfName.Get("Function"), composed);
            if (read.GetArray(PdfName.Get("Background")) is { Count: var count } background && count == wide.Inputs)
                result.Set(PdfName.Get("Background"), new PdfArray([.. wide.Convert([.. Enumerable.Range(0, count).Select(i => background.Get(i).AsNumber() ?? 0)])
                    .Select(v => (PdfObject)new PdfReal(Math.Round(v, 6)))]));
            return result;
        }

        /// <summary>The function of a shading followed by the tint transform of its space (see <see cref="WithoutWideShading"/>), written.</summary>
        private PdfObject? Composed(PdfObject function, WideSpace wide, Source source, int inputs)
        {
            var doc = source.Doc;
            if (Look(function, source) is (PdfStream first, var firstFrom) && first.Dictionary.GetInteger(PdfName.Get("FunctionType")) == 4 &&
                wide.TintRaw is not null && Look(wide.TintRaw, wide.From) is (PdfStream second, var secondFrom) && second.Dictionary.GetInteger(PdfName.Get("FunctionType")) == 4 &&
                Program(first, firstFrom) is { } shadingProgram && Program(second, secondFrom) is { } tintProgram &&
                first.Dictionary.GetArray(PdfName.Get("Domain")) is { } domain && second.Dictionary.GetArray(PdfName.Get("Range")) is { } range &&
                second.Dictionary.GetArray(PdfName.Get("Domain")) is { } tintDomain)
            {
                // the values between the two clipped as the display clips them: to the range of the first and the domain of the second
                var between = first.Dictionary.GetArray(PdfName.Get("Range"));
                var clip = new StringBuilder();
                for (var c = 0; c < wide.Inputs; c++)
                {
                    var low = Math.Max(Number(between, 2 * c, double.MinValue), Number(tintDomain, 2 * c, double.MinValue));
                    var high = Math.Min(Number(between, 2 * c + 1, double.MaxValue), Number(tintDomain, 2 * c + 1, double.MaxValue));
                    if (low > double.MinValue) clip.Append(CultureInfo.InvariantCulture, $"dup {Real(low)} lt {{ pop {Real(low)} }} if ");
                    if (high < double.MaxValue) clip.Append(CultureInfo.InvariantCulture, $"dup {Real(high)} gt {{ pop {Real(high)} }} if ");
                    clip.Append(CultureInfo.InvariantCulture, $"{wide.Inputs} 1 roll ");
                }
                var dict = new PdfDictionary();
                dict.Set(PdfName.Get("FunctionType"), PdfInteger.Of(4));
                dict.Set(PdfName.Get("Domain"), Numbers(domain));
                dict.Set(PdfName.Get("Range"), Numbers(range));
                return Fresh(new PdfStream(dict, Encoding.ASCII.GetBytes("{ " + shadingProgram + "\n" + clip + "\n" + tintProgram + " }")));
            }
            // sampled: the function as the display reads it (one or an array of functions), then the tint transform
            if (PdfFunction.CreateArray(PdfAResolve(function, source) ?? PdfNull.Instance, doc) is not { } shading || shading.Inputs > 2) return null;
            var domainOf = (PdfAResolve(function, source) switch
            {
                PdfArray functions when functions.Count > 0 => PdfAResolve(functions[0], source),
                var single => single,
            }) switch
            {
                PdfStream s => s.Dictionary.GetArray(PdfName.Get("Domain")),
                PdfDictionary d => d.GetArray(PdfName.Get("Domain")),
                _ => null,
            };
            var size = inputs == 1 ? 4096 : 128;
            var lows = new double[inputs];
            var highs = new double[inputs];
            for (var i = 0; i < inputs; i++) (lows[i], highs[i]) = (Number(domainOf, 2 * i, 0), Number(domainOf, 2 * i + 1, 1));
            var outputs = wide.Outputs;
            var data = new byte[(inputs == 1 ? size : size * size) * outputs * 2];
            var input = new double[inputs];
            var middle = new double[Math.Max(shading.Outputs, wide.Inputs)];
            var at = 0;
            for (var y = 0; y < (inputs == 1 ? 1 : size); y++)
                for (var x = 0; x < size; x++)
                {
                    input[0] = lows[0] + (highs[0] - lows[0]) * x / (size - 1);
                    if (inputs == 2) input[1] = lows[1] + (highs[1] - lows[1]) * y / (size - 1);
                    Array.Clear(middle);
                    shading.Evaluate(input, middle);
                    foreach (var v in wide.Convert(middle[..wide.Inputs]))
                    {
                        var sample = (ushort)Math.Clamp((int)Math.Round(v * 65535), 0, 65535);
                        data[at++] = (byte)(sample >> 8);
                        data[at++] = (byte)sample;
                    }
                }
            var sampled = new PdfDictionary();
            sampled.Set(PdfName.Get("FunctionType"), PdfInteger.Of(0));
            sampled.Set(PdfName.Get("Domain"), new PdfArray([.. Enumerable.Range(0, inputs).SelectMany(i => new PdfObject[] { new PdfReal(lows[i]), new PdfReal(highs[i]) })]));
            sampled.Set(PdfName.Get("Range"), new PdfArray([.. Enumerable.Range(0, outputs).SelectMany(_ => new PdfObject[] { PdfInteger.Of(0), PdfInteger.Of(1) })]));
            sampled.Set(PdfName.Get("Size"), new PdfArray([.. Enumerable.Repeat<PdfObject>(PdfInteger.Of(size), inputs)]));
            sampled.Set(PdfName.Get("BitsPerSample"), PdfInteger.Of(16));
            sampled.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
            return Fresh(new PdfStream(sampled, Flate.Encode(data)));
        }

        /// <summary>The program of a PostScript calculator without its outer braces; null if it cannot be read.</summary>
        private static string? Program(PdfStream function, Source? source)
        {
            if (source is null) return null;
            try
            {
                var text = Encoding.Latin1.GetString(source.Doc.DecodeStream(function).Data);
                var (open, close) = (text.IndexOf('{'), text.LastIndexOf('}'));
                return open >= 0 && close > open ? text[(open + 1)..close] : null;
            }
            catch (PdfException)
            {
                return null;
            }
        }

        private static double Number(PdfArray? array, int index, double otherwise) =>
            array is not null && index < array.Count && array.Get(index).AsNumber() is { } value ? value : otherwise;

        private static string Real(double value) => PdfSerializer.FormatReal(value);

        private static PdfArray Numbers(PdfArray array) => new([.. Enumerable.Range(0, array.Count).Select(i => (PdfObject)new PdfReal(array.Get(i).AsNumber() ?? 0))]);
    }
}
