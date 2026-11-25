using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Graphics;

/// <summary>A colour as 8-bit sRGB.</summary>
/// <param name="R">Red, 0–255.</param>
/// <param name="G">Green, 0–255.</param>
/// <param name="B">Blue, 0–255.</param>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Black (0, 0, 0).</summary>
    public static readonly Rgb Black = new(0, 0, 0);

    /// <summary>White (255, 255, 255).</summary>
    public static readonly Rgb White = new(255, 255, 255);

    /// <summary>Creates a colour from components in the range 0–1.</summary>
    /// <param name="r">Red, 0–1 (clamped).</param>
    /// <param name="g">Green, 0–1 (clamped).</param>
    /// <param name="b">Blue, 0–1 (clamped).</param>
    /// <returns>The colour.</returns>
    public static Rgb FromUnit(double r, double g, double b) => new(ToByte(r), ToByte(g), ToByte(b));

    /// <summary>Converts a value in the range 0–1 into 0–255, rounded and clamped; NaN gives 0.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The byte value.</returns>
    public static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round((double.IsNaN(v) ? 0 : v) * 255), 0, 255);

    /// <summary>Returns the colour as 32-bit ARGB.</summary>
    /// <param name="alpha">Opacity, 0–1 (clamped).</param>
    /// <returns>The ARGB value (alpha in the top byte).</returns>
    public uint ToArgb(double alpha) => ((uint)ToByte(alpha) << 24) | ((uint)R << 16) | ((uint)G << 8) | B;
}

/// <summary>
/// Colour spaces (ISO 32000-2, 8.6). Converts components into sRGB for display. DeviceGray and DeviceRGB directly (as sRGB),
/// DeviceCMYK, ICCBased and the CIE-based spaces through colour management (ColorSpace.Managed.cs).
/// </summary>
/// <remarks>
/// Instances are shared and used from several threads at the same time (images are converted in parallel bands).
/// </remarks>
public abstract partial class ColorSpace
{
    /// <summary>DeviceGray, taken as sRGB grey.</summary>
    public static readonly ColorSpace DeviceGray = new DeviceGraySpace();

    /// <summary>DeviceRGB, taken as sRGB.</summary>
    public static readonly ColorSpace DeviceRgb = new DeviceRgbSpace();

    /// <summary>DeviceCMYK through the bundled FOGRA39 profile; for a document use <see cref="DeviceCmykOf"/>, which honours its output intent.</summary>
    public static readonly ColorSpace DeviceCmyk = new DeviceCmykSpace();

    /// <summary>Number of colour components (0 for a pattern space without an underlying space).</summary>
    public abstract int Components { get; }

    /// <summary>Initial colour after the colour space is set (ISO 32000-2, 8.6.8).</summary>
    public virtual double[] InitialColor => new double[Components];

    /// <summary>"Paint nothing" (Separation or DeviceN with only /None).</summary>
    public virtual bool PaintsNothing => false;

    /// <summary>Pattern space; the display list carries patterns as shadings or tiling patterns, the colour here is only a fallback.</summary>
    public virtual bool IsPattern => false;

    /// <summary>Palette (Indexed): samples are indices.</summary>
    public virtual bool IsIndexed => false;

    /// <summary>The base space of an indexed space (its palette colours are in it); null for every other space.</summary>
    internal virtual ColorSpace? IndexedBase => null;

    /// <summary>
    /// The components in <see cref="IndexedBase"/> of the palette entry nearest to <paramref name="index"/> (a half upwards, as <see cref="ToRgb"/>);
    /// null for every space but an indexed one. A shading blends these, never the indices (ISO 32000-2, 8.7.4.4).
    /// </summary>
    internal virtual double[]? IndexedComponents(double index) => null;

    /// <summary>The underlying space of a Pattern space (an uncoloured pattern takes its colour there); null for every other space.</summary>
    internal virtual ColorSpace? PatternUnderlying => null;

    /// <summary>Separation or DeviceN: paints only its own colourants (relevant for overprint, ISO 32000-2, 11.7.4).</summary>
    public virtual bool IsColorant => false;

    /// <summary>Converts a colour of this space into sRGB.</summary>
    /// <param name="components">The components in the units of the space (normally 0–1; Lab: L 0–100, a/b by /Range; Indexed: the index).</param>
    /// <returns>The colour; never throws for too few components but returns a fallback (black for the device and CIE-based spaces).</returns>
    public abstract Rgb ToRgb(ReadOnlySpan<double> components);

    /// <summary>Default /Decode for images: [min max] of the sample range per component.</summary>
    /// <param name="bitsPerComponent">Bits per sample (only relevant for Indexed, whose range is 0 to 2^bits − 1).</param>
    /// <returns>Two numbers per component.</returns>
    public virtual double[] DefaultDecode(int bitsPerComponent) =>
        Enumerable.Range(0, Components).SelectMany(_ => new[] { 0.0, 1.0 }).ToArray();

    /// <summary>
    /// Reads a colour space from a name or an array. Names other than the device names are looked up in
    /// /Resources /ColorSpace. Anything unreadable becomes DeviceGray (never an exception).
    /// </summary>
    /// <param name="obj">The name or array.</param>
    /// <param name="resources">Resources for named colour spaces and /Default… spaces; may be null.</param>
    /// <param name="doc">The document (stream data, cache, output intent).</param>
    /// <param name="depth">Nesting depth; deeper than 8 gives DeviceGray (protection against cycles).</param>
    /// <returns>The colour space; arrays are built only once per object and document.</returns>
    public static ColorSpace Create(PdfObject obj, PdfDictionary? resources, PdfDocument doc, int depth = 0) =>
        // build colour spaces given as arrays (ICC profiles, separation colours with functions, palettes) only once per object
        obj is PdfArray && depth <= 8 ? doc.CachedColorSpace(obj, () => CreateUncached(obj, resources, doc, depth)) : CreateUncached(obj, resources, doc, depth);

    private static ColorSpace CreateUncached(PdfObject obj, PdfDictionary? resources, PdfDocument doc, int depth)
    {
        if (depth > 8) return DeviceGray;
        try
        {
            switch (obj)
            {
                case PdfName name:
                    // the abbreviations of inline images (G, RGB, CMYK, I) are ordinary names elsewhere: a resource of that name is that
                    // resource (8.6.8; before, /G [/Indexed …] drew in DeviceGray — docs/FEHLERGEDAECHTNIS.md, No. 1126)
                    if (name.Value is "G" or "RGB" or "CMYK" or "I"
                        && resources?.GetDictionary(PdfName.Get("ColorSpace"))?.Get(name) is { } listed and not PdfNull && !ReferenceEquals(listed, obj))
                        return Create(listed, resources, doc, depth + 1);
                    switch (name.Value)
                    {
                        case "DeviceGray" or "G": return Device(1, resources, doc);
                        case "DeviceRGB" or "RGB": return Device(3, resources, doc);
                        case "DeviceCMYK" or "CMYK": return Device(4, resources, doc);
                        case "CalGray": return DeviceGray;
                        case "CalRGB": return DeviceRgb;
                        // CalCMYK is treated as DeviceCMYK (8.6.5.1); before, it fell to DeviceGray (docs/FEHLERGEDAECHTNIS.md, No. 1123)
                        case "CalCMYK": return Device(4, resources, doc);
                        case "Pattern": return new PatternSpace(null);
                        case "I" or "Indexed": return DeviceGray;
                    }
                    var named = resources?.GetDictionary(PdfName.Get("ColorSpace"))?.Get(name);
                    return named is null or PdfNull || ReferenceEquals(named, obj) ? DeviceGray : Create(named, resources, doc, depth + 1);

                case PdfArray { Count: > 0 } array:
                    var family = (array.Get(0) as PdfName)?.Value;
                    return family switch
                    {
                        "DeviceGray" or "G" => DeviceGray,
                        "DeviceRGB" or "RGB" => DeviceRgb,
                        "DeviceCMYK" or "CMYK" => DeviceCmykOf(doc),
                        "CalGray" => new CalGraySpace(array.Count > 1 ? array.Get(1).AsDictionary() : null),
                        "CalRGB" => new CalRgbSpace(array.Count > 1 ? array.Get(1).AsDictionary() : null),
                        "CalCMYK" => DeviceCmykOf(doc),
                        "Lab" => new LabSpace(array.Get(1).AsDictionary()),
                        "ICCBased" => IccBased(array.Get(1), resources, doc, depth),
                        "Indexed" or "I" => IndexedSpace.Create(array, resources, doc, depth),
                        "Separation" => SeparationSpace.Create(array, resources, doc, depth, deviceN: false),
                        "DeviceN" => SeparationSpace.Create(array, resources, doc, depth, deviceN: true),
                        "Pattern" => new PatternSpace(array.Count > 1 ? Create(array.Get(1), resources, doc, depth + 1) : null),
                        _ => DeviceGray,
                    };
                default:
                    return DeviceGray;
            }
        }
        catch (PdfException)
        {
            return DeviceGray;
        }
    }

    // ------------------------------------------------------------ Device colours

    private sealed class DeviceGraySpace : ColorSpace
    {
        public override int Components => 1;

        public override Rgb ToRgb(ReadOnlySpan<double> c)
        {
            var g = Rgb.ToByte(c.Length > 0 ? c[0] : 0);
            return new Rgb(g, g, g);
        }
    }

    private sealed class DeviceRgbSpace : ColorSpace
    {
        public override int Components => 3;

        public override Rgb ToRgb(ReadOnlySpan<double> c) =>
            c.Length >= 3 ? Rgb.FromUnit(c[0], c[1], c[2]) : Rgb.Black;
    }

    /// <summary>DeviceCMYK without an output profile: the bundled FOGRA39 profile (<see cref="Icc.CmykProfile"/>), also for images and CMYK JPEGs.</summary>
    private sealed class DeviceCmykSpace : ColorSpace
    {
        public override int Components => 4;

        public override double[] InitialColor => [0, 0, 0, 1];

        public override bool IsDeviceCmyk => true;

        public override Rgb ToRgb(ReadOnlySpan<double> c) =>
            c.Length >= 4 ? Icc.CmykProfile.Default.ToRgb(c) : Rgb.Black;

        // no variants: without an output profile in the document DeviceCMYK is a device space, and a rendering intent applies only to CIE-based
        // conversions (ISO 32000-2, 8.6.5.8) — the bundled profile only models the device; with an output profile the IccSpace reacts
        // (docs/FEHLERGEDAECHTNIS.md, No. 572)

        internal override bool HasRow8 => true;

        internal override void ConvertRow8(ReadOnlySpan<byte> row, int width, Span<byte> rgba) => Icc.CmykProfile.Default.Row8(row, width, rgba);

    }

    // ------------------------------------------------------------ CIE Lab

    private sealed class LabSpace : ColorSpace
    {
        private readonly double _xw = 0.9505, _yw = 1, _zw = 1.089;
        private readonly double[] _range = [-100, 100, -100, 100];

        private readonly float[] _white;
        private readonly PdfDictionary? _dict;
        private readonly CieMapping _mapping;

        public LabSpace(PdfDictionary? dict, byte intent = RelativeColorimetric, bool compensate = true)
        {
            _dict = dict;
            if (Numbers(dict?.GetArray(PdfName.Get("WhitePoint"))) is { Length: 3 } wp && wp[1] > 0) (_xw, _yw, _zw) = (wp[0], wp[1], wp[2]);
            _white = [(float)_xw, (float)_yw, (float)_zw];
            // only a sensible range (finite, minimum not above maximum): otherwise Math.Clamp would throw during conversion (found by fuzzing)
            if (Numbers(dict?.GetArray(PdfName.Get("Range"))) is { Length: 4 } r && r.All(double.IsFinite) && r[0] <= r[1] && r[2] <= r[3]) _range = r;
            // /BlackPoint and the rendering intent (8.6.5.4, 8.6.5.8; docs/FEHLERGEDAECHTNIS.md, No. 1129)
            _mapping = CieMapping.For(_white, BlackPoint(dict), intent, compensate);
        }

        public override int Components => 3;

        private protected override bool DependsOnRendering => true;

        private protected override ColorSpace Rendered(byte intent, bool compensate) =>
            CieMapping.For(_white, BlackPoint(_dict), intent, compensate).Same(_mapping) ? this : new LabSpace(_dict, intent, compensate);

        public override double[] DefaultDecode(int bpc) => [0, 100, _range[0], _range[1], _range[2], _range[3]];

        public override Rgb ToRgb(ReadOnlySpan<double> c)
        {
            if (c.Length < 3) return Rgb.Black;
            var l = Math.Clamp(c[0], 0, 100);
            var a = Math.Clamp(c[1], _range[0], _range[1]);
            var b = Math.Clamp(c[2], _range[2], _range[3]);
            var fy = (l + 16) / 116;
            var fx = fy + a / 500;
            var fz = fy - b / 200;
            static double G(double t) => t >= 6.0 / 29 ? t * t * t : 108.0 / 841 * (t - 4.0 / 29);
            // XYZ under the white point of the colour space, adapted to D50 → sRGB (assuming D65 instead gives a yellow cast with a D50 white point)
            Span<float> xyz = [(float)(_xw * G(fx)), (float)(_yw * G(fy)), (float)(_zw * G(fz))];
            return _mapping.ToRgb(_white, xyz);
        }
    }

    // ------------------------------------------------------------ Indexed

    private sealed class IndexedSpace : ColorSpace
    {
        private readonly Rgb[] _palette;

        private readonly ColorSpace _base;
        private readonly byte[] _lookup;

        private IndexedSpace(Rgb[] palette, ColorSpace baseSpace, byte[] lookup) => (_palette, _base, _lookup) = (palette, baseSpace, lookup);

        internal override ColorSpace? IndexedBase => _base;

        private protected override bool DependsOnRendering => _base.DependsOnRendering;

        // the palette again through the base space's variant (ISO 32000-2, 8.6.5.8: the intent applies to the base)
        private protected override ColorSpace Rendered(byte intent, bool compensate)
        {
            var variant = _base.WithRendering(intent, compensate ? BpcDefault : BpcOff);
            return ReferenceEquals(variant, _base) ? this : new IndexedSpace(Palette(variant, _palette.Length - 1, _lookup), variant, _lookup);
        }

        public static ColorSpace Create(PdfArray array, PdfDictionary? resources, PdfDocument doc, int depth)
        {
            if (array.Count < 4) return DeviceGray;
            var baseSpace = ColorSpace.Create(array.Get(1), resources, doc, depth + 1);
            var hival = Math.Clamp((int)(array.GetNumber(2) ?? 0), 0, 255);
            var lookup = array.Get(3) switch
            {
                PdfString s => s.Bytes,
                PdfStream st => doc.GetStreamData(st),
                _ => [],
            };
            return new IndexedSpace(Palette(baseSpace, hival, lookup), baseSpace, lookup);
        }

        private static Rgb[] Palette(ColorSpace baseSpace, int hival, byte[] lookup)
        {
            var n = baseSpace.Components;
            var palette = new Rgb[hival + 1];
            Span<double> comps = stackalloc double[Math.Max(n, 1)];
            // map entries 0…255 onto the value range of the base (ISO 32000-2, 8.6.6.3) — Lab: L 0…100, a/b by /Range (issue10339 is black otherwise)
            var range = baseSpace.DefaultDecode(8);
            for (var i = 0; i <= hival; i++)
            {
                for (var k = 0; k < n; k++)
                {
                    var idx = i * n + k;
                    var (min, max) = 2 * k + 1 < range.Length ? (range[2 * k], range[2 * k + 1]) : (0.0, 1.0);
                    comps[k] = min + (idx < lookup.Length ? lookup[idx] / 255.0 : 0) * (max - min);
                }
                palette[i] = baseSpace.ToRgb(comps[..n]);
            }
            return palette;
        }

        public override int Components => 1;

        public override double[] DefaultDecode(int bpc) => [0, (1 << bpc) - 1];

        public override bool IsIndexed => true;

        public override Rgb ToRgb(ReadOnlySpan<double> c) => _palette[Entry(c.Length > 0 ? c[0] : 0)];

        // to the nearest entry, a half upwards (8.6.6.3, PDF 2.0; before, to even: 2.5 gave 2 — docs/FEHLERGEDAECHTNIS.md, No. 1123)
        private int Entry(double index) =>
            Math.Clamp(double.IsFinite(index) ? (int)Math.Clamp(Math.Floor(index + 0.5), int.MinValue, int.MaxValue) : 0, 0, _palette.Length - 1);

        internal override double[] IndexedComponents(double index)
        {
            // as Palette maps the bytes of the table onto the value range of the base
            var n = _base.Components;
            var range = _base.DefaultDecode(8);
            var entry = Entry(index);
            var comps = new double[n];
            for (var k = 0; k < n; k++)
            {
                var idx = entry * n + k;
                var (min, max) = 2 * k + 1 < range.Length ? (range[2 * k], range[2 * k + 1]) : (0.0, 1.0);
                comps[k] = min + (idx < _lookup.Length ? _lookup[idx] / 255.0 : 0) * (max - min);
            }
            return comps;
        }
    }

    // ------------------------------------------------------------ Separation, DeviceN

    private sealed class SeparationSpace : ColorSpace
    {
        public override bool IsColorant => true;

        private readonly int _components;
        private readonly ColorSpace _alternate;
        private readonly PdfFunction? _tint;
        private readonly bool _none;
        private readonly bool _all;
        private readonly ColorCache _cache = new();

        private SeparationSpace(int components, ColorSpace alternate, PdfFunction? tint, bool none, bool all)
        {
            _components = components;
            _alternate = alternate;
            _tint = tint;
            _none = none;
            _all = all;
        }

        public static ColorSpace Create(PdfArray array, PdfDictionary? resources, PdfDocument doc, int depth, bool deviceN)
        {
            if (array.Count < 4) return DeviceGray;
            var names = deviceN ? (array.Get(1) as PdfArray)?.Select(o => (o as PdfName)?.Value).ToList() : [(array.Get(1) as PdfName)?.Value];
            if (names is null || names.Count == 0 || names.Count > PdfLimits.MaxComponents) return DeviceGray;
            var alternate = ColorSpace.Create(array.Get(2), resources, doc, depth + 1);
            var tint = PdfFunction.Create(array.Get(3), doc);
            var none = names.All(n => n == "None");
            var all = !deviceN && names[0] == "All";
            return new SeparationSpace(names.Count, alternate, tint, none, all);
        }

        public override int Components => _components;

        private protected override bool DependsOnRendering => _alternate.DependsOnRendering;

        private protected override ColorSpace Rendered(byte intent, bool compensate)
        {
            var variant = _alternate.WithRendering(intent, compensate ? BpcDefault : BpcOff);
            return ReferenceEquals(variant, _alternate) ? this : new SeparationSpace(_components, variant, _tint, _none, _all);
        }

        public override double[] InitialColor => Enumerable.Repeat(1.0, _components).ToArray();

        public override bool PaintsNothing => _none;

        public override Rgb ToRgb(ReadOnlySpan<double> c)
        {
            if (_all)
            {
                // "All": on all separations; on screen like black in the given tint
                var g = Rgb.ToByte(1 - (c.Length > 0 ? c[0] : 1));
                return new Rgb(g, g, g);
            }

            // cache the results (images with separation colours call this millions of times); the key encodes at most 6 components
            // in 0 … 1 and their number uniquely, so with more (DeviceN) or values outside there is no cache — otherwise [0, 1.031] and
            // [0.001, 0] shared a key and the second got the colour of the first (docs/FEHLERGEDAECHTNIS.md, No. 272)
            var cacheable = c.Length <= 6;
            long key = c.Length;
            for (var i = 0; i < Math.Min(c.Length, 6); i++)
            {
                if (!(c[i] >= 0 && c[i] <= 1)) cacheable = false;
                key = key * 1031 + (long)Math.Round(Math.Clamp(c[i], 0, 1) * 1000);
            }
            if (cacheable && _cache.TryGet(key, out var cached)) return cached;
            // cached: computed from the values of the key (rounded to thousandths), so that the colour does not depend on which value of
            // its step came first (docs/FEHLERGEDAECHTNIS.md, No. 400)
            Span<double> rounded = stackalloc double[c.Length];
            for (var i = 0; i < c.Length; i++) rounded[i] = cacheable ? Math.Round(c[i] * 1000) / 1000 : c[i];

            Span<double> alt = stackalloc double[Math.Max(Math.Max(_alternate.Components, _tint?.Outputs ?? 0), 8)];
            if (_tint is null)
            {
                alt.Clear();
            }
            else
            {
                alt.Clear();
                _tint.Evaluate(rounded, alt);
            }
            var rgb = _alternate.ToRgb(alt[.._alternate.Components]);
            if (cacheable) _cache.Add(key, rgb);
            return rgb;
        }
    }

    // ------------------------------------------------------------ Pattern (fallback colour for broken patterns)

    private sealed class PatternSpace(ColorSpace? underlying) : ColorSpace
    {
        /// <summary>Light grey as the fallback for patterns that cannot be read.</summary>
        public static readonly Rgb Placeholder = new(200, 200, 200);

        public override int Components => underlying?.Components ?? 0;

        public override bool IsPattern => true;

        internal override ColorSpace? PatternUnderlying => underlying;

        public override Rgb ToRgb(ReadOnlySpan<double> c) =>
            underlying is not null && c.Length >= underlying.Components ? underlying.ToRgb(c[..underlying.Components]) : Placeholder;
    }

    private static double[]? Numbers(PdfArray? array) => PdfFunction.Numbers(array);
}
