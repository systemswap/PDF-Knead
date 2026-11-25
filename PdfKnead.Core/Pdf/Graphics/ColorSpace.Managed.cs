using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Graphics;

/// <summary>
/// Colour management: ICC profiles, CalGray/CalRGB/Lab through XYZ, DeviceCMYK through the output profile of the document or the
/// bundled FOGRA39 profile, /DefaultGray, /DefaultRGB, /DefaultCMYK of the resources (8.6.5.6). The target is always sRGB.
/// </summary>
public abstract partial class ColorSpace
{
    /// <summary>DeviceCMYK as meant by overprint mode 1 and the operators k/K, also when it uses an output profile.</summary>
    public virtual bool IsDeviceCmyk => false;

    /// <summary>Rendering intents (ISO 32000-2, Table 69), as the graphics state carries them; anything unknown counts as relative colorimetric.</summary>
    public const byte Perceptual = 0, RelativeColorimetric = 1, Saturation = 2, AbsoluteColorimetric = 3;

    /// <summary>Black point compensation of the graphics state (/UseBlackPtComp, ISO 32000-2, 8.6.5.9): Default (the program's choice: on), ON, OFF.</summary>
    public const byte BpcDefault = 0, BpcOn = 1, BpcOff = 2;

    /// <summary>A rendering intent from its name (/RI, /Intent, the operand of ri); unknown names count as relative colorimetric.</summary>
    public static byte IntentFrom(PdfObject? name) => (name as PdfName)?.Value switch
    {
        "Perceptual" => Perceptual,
        "Saturation" => Saturation,
        "AbsoluteColorimetric" => AbsoluteColorimetric,
        _ => RelativeColorimetric,
    };

    /// <summary>/UseBlackPtComp from its name; anything else counts as Default.</summary>
    public static byte BpcFrom(PdfObject? name) => (name as PdfName)?.Value switch { "ON" => BpcOn, "OFF" => BpcOff, _ => BpcDefault };

    private System.Collections.Concurrent.ConcurrentDictionary<int, ColorSpace>? _renderings;

    /// <summary>
    /// This colour space for a rendering intent and black point compensation (ISO 32000-2, 8.6.5.8, 8.6.5.9): ICC-based spaces take the
    /// table of the intent and compensate the black point after ISO 18619 unless /UseBlackPtComp is OFF or the intent is absolute
    /// colorimetric, which instead renders the media white of the profile (the paper tone); DeviceCMYK through its profile likewise; CalGray,
    /// CalRGB and Lab map their /BlackPoint onto the black of the display unless OFF or absolute, and with absolute colorimetric take their
    /// XYZ without adapting their white point; /Indexed and /Separation, /DeviceN pass it on to their base or alternate space. Device spaces
    /// are themselves. Relative colorimetric with Default or ON is this space as before. Variants are shared per space
    /// (docs/FEHLERGEDAECHTNIS.md, No. 572, No. 1129).
    /// </summary>
    /// <param name="intent">One of <see cref="Perceptual"/> … <see cref="AbsoluteColorimetric"/>.</param>
    /// <param name="bpc">One of <see cref="BpcDefault"/>, <see cref="BpcOn"/>, <see cref="BpcOff"/>.</param>
    /// <returns>This space or its variant.</returns>
    public ColorSpace WithRendering(byte intent, byte bpc)
    {
        if (intent > AbsoluteColorimetric) intent = RelativeColorimetric;
        var compensate = bpc != BpcOff && intent != AbsoluteColorimetric;
        if (intent == RelativeColorimetric && compensate || !DependsOnRendering) return this;
        var renderings = _renderings ?? System.Threading.Interlocked.CompareExchange(ref _renderings, new(), null) ?? _renderings!;
        return renderings.GetOrAdd(intent * 2 + (compensate ? 1 : 0), _ => Rendered(intent, compensate));
    }

    /// <summary>Whether <see cref="WithRendering"/> can give anything but this space.</summary>
    private protected virtual bool DependsOnRendering => false;

    /// <summary>The variant for a rendering intent (0 perceptual … 3 absolute colorimetric) and black point compensation.</summary>
    private protected virtual ColorSpace Rendered(byte intent, bool compensate) => this;

    /// <summary>Can convert whole 8-bit rows quickly (tables instead of a calculation per pixel).</summary>
    internal virtual bool HasRow8 => false;

    /// <summary>
    /// A colour as exactly as the space can give it, without the rounding a cache of single colours may apply (an ICC space rounds to
    /// thousandths for its cache); for images drawn at their full depth (docs/FEHLERGEDAECHTNIS.md, No. 1092).
    /// </summary>
    internal virtual Rgb ToRgbExact(ReadOnlySpan<double> components) => ToRgb(components);

    /// <summary>Row with 8 bits per component → RGBA (opaque); only if <see cref="HasRow8"/>.</summary>
    internal virtual void ConvertRow8(ReadOnlySpan<byte> row, int width, Span<byte> rgba) => throw new NotSupportedException();


    /// <summary>
    /// DeviceCMYK of this document: the CMYK output profile (/OutputIntents, for example PDF/X), otherwise the bundled FOGRA39 profile,
    /// as in common PDF editors.
    /// </summary>
    /// <param name="doc">The document; the result is cached in it.</param>
    /// <returns>The colour space for DeviceCMYK.</returns>
    public static ColorSpace DeviceCmykOf(PdfDocument doc) =>
        _pageCmyk ?? (doc.DeviceCmykSpace ??= OutputIntentCmyk(doc.Catalog?.GetArray(PdfName.Get("OutputIntents")), doc) ?? DeviceCmyk);

    // DeviceCMYK of the page being interpreted or the image being decoded, where the page has output intents of its own (PDF 2.0); the
    // work on a page and on one image runs on one thread
    [ThreadStatic] private static ColorSpace? _pageCmyk;

    /// <summary>Whether DeviceCMYK currently belongs to a page (colour spaces built now must not be shared across the document).</summary>
    internal static bool PageScoped => _pageCmyk is not null;

    /// <summary>
    /// DeviceCMYK of a page with its own /OutputIntents (ISO 32000-2, 14.11.5, Tables 401 and 402: they apply before those of the catalog);
    /// null if it has none with a usable CMYK profile (then the document's applies). Cached per page in the document.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page dictionary.</param>
    /// <returns>The colour space, or null.</returns>
    public static ColorSpace? PageCmyk(PdfDocument doc, PdfDictionary page) =>
        page.GetArray(PdfName.Get("OutputIntents")) is { Count: > 0 } intents
            ? doc.PageCmykSpaces.GetValue(page, _ => new StrongBox(OutputIntentCmyk(intents, doc))).Value
            : null;

    internal sealed class StrongBox(ColorSpace? value)
    {
        public ColorSpace? Value { get; } = value;
    }

    /// <summary>Uses <paramref name="cmyk"/> for DeviceCMYK on this thread until disposed; null changes nothing.</summary>
    internal static PageCmykScope ForPage(ColorSpace? cmyk) => new(cmyk);

    internal readonly struct PageCmykScope : IDisposable
    {
        private readonly ColorSpace? _previous;
        private readonly bool _set;

        public PageCmykScope(ColorSpace? cmyk)
        {
            _previous = _pageCmyk;
            _set = cmyk is not null;
            if (_set) _pageCmyk = cmyk;
        }

        public void Dispose()
        {
            if (_set) _pageCmyk = _previous;
        }
    }

    /// <summary>
    /// The first usable CMYK profile of output intents: /DestOutputProfile, else an embedded file of /DestOutputProfileRef /URLs (ISO 32000-2,
    /// Table 402; addresses are never fetched — the program works offline, KONZEPT section 4; /CheckSum is not checked, the profile is
    /// parsed like any ICC profile; docs/FEHLERGEDAECHTNIS.md, No. 575).
    /// </summary>
    private static ColorSpace? OutputIntentCmyk(PdfArray? intents, PdfDocument doc)
    {
        if (intents is null) return null;
        try
        {
            for (var i = 0; i < Math.Min(intents.Count, 16); i++)
            {
                if (intents.Get(i).AsDictionary() is not { } intent) continue;
                foreach (var stream in Profiles(intent))
                {
                    if (stream.Dictionary.GetInteger(PdfName.N) is { } n && n != 4) continue;
                    // within the document's budget, once per profile (docs/FEHLERGEDAECHTNIS.md, No. 1122)
                    if (ProfileSpace(doc.GetStreamData(stream), 4, doc, deviceCmyk: true) is { } space)
                        return space;
                }
            }
        }
        catch (PdfException)
        {
        }
        return null;

        static IEnumerable<PdfStream> Profiles(PdfDictionary intent)
        {
            if (intent.Get(PdfName.Get("DestOutputProfile")) is PdfStream own)
            {
                yield return own;
                yield break;
            }
            if (intent.GetDictionary(PdfName.Get("DestOutputProfileRef"))?.GetArray(PdfName.Get("URLs")) is not { } urls) yield break;
            // the embedded file under /UF before /F, as readers take /UF (ISO 32000-2, 7.11.3, Table 43); before, /F only — Get gives the
            // null object for a missing key, so the fall-back to /UF never came (docs/FEHLERGEDAECHTNIS.md, No. 789)
            for (var u = 0; u < Math.Min(urls.Count, 16); u++)
                if (urls.Get(u).AsDictionary()?.GetDictionary(PdfName.Get("EF")) is { } files
                    && (files.Get(PdfName.Get("UF")) is PdfStream unicode ? unicode : files.Get(PdfName.Get("F"))) is PdfStream embedded)
                    yield return embedded;
        }
    }

    /// <summary>
    /// Device colour space, honouring /DefaultGray, /DefaultRGB, /DefaultCMYK in the resources (they must have the same number of
    /// components and must not be a pattern or palette space, otherwise the device space applies).
    /// </summary>
    /// <param name="components">1, 3 or 4 (any other value is treated as 4).</param>
    /// <param name="resources">The resources to look in; may be null.</param>
    /// <param name="doc">The document (output intent for DeviceCMYK).</param>
    /// <returns>The colour space.</returns>
    public static ColorSpace Device(int components, PdfDictionary? resources, PdfDocument doc)
    {
        var name = components switch { 1 => "DefaultGray", 3 => "DefaultRGB", _ => "DefaultCMYK" };
        // inside a default colour space the device colours themselves apply (substitute for an unreadable ICC profile, /Alternate);
        // otherwise /DefaultRGB → ICC (broken) → DeviceRGB → /DefaultRGB … ends in a stack overflow (found by fuzzing)
        if (!_resolvingDefault && resources?.GetDictionary(PdfName.Get("ColorSpace"))?.Get(PdfName.Get(name)) is { } custom and not PdfNull and not PdfName)
        {
            _resolvingDefault = true;
            ColorSpace space;
            try
            {
                space = Create(custom, resources, doc, 1);
            }
            finally
            {
                _resolvingDefault = false;
            }
            if (space.Components == components && !space.IsPattern && !space.IsIndexed) return space;
        }
        return components switch { 1 => DeviceGray, 3 => DeviceRgb, _ => DeviceCmykOf(doc) };
    }

    [ThreadStatic] private static bool _resolvingDefault;

    /// <summary>Whether the resources name /DefaultGray, /DefaultRGB or /DefaultCMYK for this number of components (whether usable or not).</summary>
    /// <param name="components">1, 3 or 4 (any other value is treated as 4).</param>
    /// <param name="resources">The resources to look in; may be null.</param>
    internal static bool HasDefault(int components, PdfDictionary? resources) =>
        resources?.GetDictionary(PdfName.Get("ColorSpace"))?.Get(PdfName.Get(components switch { 1 => "DefaultGray", 3 => "DefaultRGB", _ => "DefaultCMYK" }))
            is { } custom and not PdfNull and not PdfName;

    /// <summary>Colour space from an embedded profile (JPEG 2000); null if it does not fit.</summary>
    internal static ColorSpace? FromProfile(byte[] data, int components, PdfDocument doc) => ProfileSpace(data, components, doc, deviceCmyk: false);

    /// <summary>
    /// The colour space of a profile given as bytes, within the document's budget for colour tables and read once per document and content
    /// (<see cref="PdfDocument.ProfileSpaces"/>): before, the profile of a JPEG 2000 image was read at every decoding and the output intent's
    /// for every page, outside any budget (docs/FEHLERGEDAECHTNIS.md, No. 1122). Null if it is unreadable, does not fit or exceeds the budget.
    /// </summary>
    private static ColorSpace? ProfileSpace(byte[] data, int components, PdfDocument doc, bool deviceCmyk)
    {
        var key = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)) + ":" + components + (deviceCmyk ? "k" : "");
        return doc.ProfileSpaces.GetOrAdd(key, _ =>
            IccProfile.Parse(data) is not { } profile || profile.Channels != components || !doc.ReserveIccTables(profile.TableEntries) ? null
            : !deviceCmyk && IsSrgb(profile) && profile.AbsoluteScale is null ? DeviceRgb : new IccSpace(Shared(profile), deviceCmyk, profile));
    }

    // Some producers attach the same profile as a separate copy to every image: share the transform (tables up to 1 MB, caches) by
    // content, also across documents — limited to 32 (then cleared); the profiles themselves are immutable. Only small profiles: the cache
    // lies outside every document's budget, 32 large tables would stay after their documents are closed (docs/FEHLERGEDAECHTNIS.md, No. 1122)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IccTransform> SharedTransforms = new();

    /// <summary>Most table entries of a profile whose transform is shared across documents (1 Mi entries, 4 MB).</summary>
    private const long SharedTableEntries = 1L << 20;

    private static IccTransform Shared(IccProfile profile, int intent = RelativeColorimetric, bool compensate = true)
    {
        if (profile.TableEntries > SharedTableEntries) return new IccTransform(profile, compensate, intent);
        var key = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(profile.Data)) + ":" + intent + (compensate ? "+" : "-");
        if (SharedTransforms.TryGetValue(key, out var shared)) return shared;
        if (SharedTransforms.Count >= 32) SharedTransforms.Clear();
        return SharedTransforms.GetOrAdd(key, _ => new IccTransform(profile, compensate, intent));
    }

    /// <summary>A variant of an ICC transform's profile for a rendering intent and black point compensation (DeviceCMYK through an output profile as well).</summary>
    private static ColorSpace RenderedIcc(IccProfile profile, bool deviceCmyk, byte intent, bool compensate) =>
        new IccSpace(Shared(profile, intent, compensate), deviceCmyk, profile);

    private static ColorSpace IccBased(PdfObject stream, PdfDictionary? resources, PdfDocument doc, int depth)
    {
        var dict = stream.AsDictionary();
        var n = (int)(dict?.GetInteger(PdfName.N) ?? 0);
        if (stream is PdfStream s && n is >= 1 and <= 15)
        {
            var data = Array.Empty<byte>();
            try
            {
                data = doc.GetStreamData(s);
            }
            catch (PdfException)
            {
            }
            // the colour tables within the document's budget, else as an unreadable profile (No. 759)
            // matrix and grey profiles too: their curves may hold tables (No. 1122)
            if (IccProfile.Parse(data) is { } profile && profile.Channels == n && doc.ReserveIccTables(profile.TableEntries))
            {
                // /Range other than 0…1: the values are clamped to it and, for a Lab or XYZ data colour space, encoded as the profile's tables
                // expect (8.6.5.5, ICC.1 6.3.4, 10.10/10.11; before, clamped to 0…1 — L* 50 drew white; docs/FEHLERGEDAECHTNIS.md, No. 1126)
                var range = PdfFunction.Numbers(dict?.GetArray(PdfName.Get("Range")));
                if (range is not null && range.Length >= 2 * n && range.Take(2 * n).All(double.IsFinite)
                    && Enumerable.Range(0, n).All(i => range[2 * i] < range[2 * i + 1])
                    && Enumerable.Range(0, n).Any(i => range[2 * i] != 0 || range[2 * i + 1] != 1))
                    return new IccSpace(Shared(profile), deviceCmyk: false, profile) { Range = range[..(2 * n)] };
                // sRGB as DeviceRGB only where absolute colorimetric gives the same (No. 1129)
                return IsSrgb(profile) && profile.AbsoluteScale is null ? DeviceRgb : new IccSpace(Shared(profile), deviceCmyk: false, profile);
            }
        }
        // profile unreadable: alternate colour space
        if (dict?.Get(PdfName.Get("Alternate")) is { } alternate and not PdfNull)
            return Create(alternate, resources, doc, depth + 1);
        return n switch
        {
            1 => DeviceGray,
            4 => DeviceCmykOf(doc),
            _ => DeviceRgb,
        };
    }

    /// <summary>
    /// sRGB as a matrix profile (often attached to every image): treated like DeviceRGB — the same colour, without the detour through tables.
    /// </summary>
    private static bool IsSrgb(IccProfile profile)
    {
        if (!profile.IsMatrixShaper || profile.Matrix is not { } m || profile.Curves is not { } curves) return false;
        float[] srgb = [0.4361f, 0.3851f, 0.1431f, 0.2225f, 0.7169f, 0.0606f, 0.0139f, 0.0971f, 0.7141f];
        for (var i = 0; i < 9; i++)
            if (Math.Abs(m[i] - srgb[i]) > 0.003f) return false;
        foreach (var curve in curves)
            foreach (var x in new[] { 0.02f, 0.1f, 0.3f, 0.5f, 0.8f, 1f })
                if (Math.Abs(curve.Evaluate(x) - IccColor.Decode(x)) > 0.004f) return false;
        return true;
    }

    // ------------------------------------------------------------ ICC

    private sealed class IccSpace(IccTransform transform, bool deviceCmyk, IccProfile source) : ColorSpace
    {
        private readonly MatrixShaper? _shaper = MatrixShaper.From(transform);

        // the profile as read (all tables), from which the variants are built
        private protected override bool DependsOnRendering => true;

        private protected override ColorSpace Rendered(byte intent, bool compensate) =>
            Range is { } range && RenderedIcc(source, deviceCmyk, intent, compensate) is IccSpace other
                ? new IccSpace(other.transform, deviceCmyk, source) { Range = range }
                : RenderedIcc(source, deviceCmyk, intent, compensate);

        // the /Range of the ICCBased dictionary where it is not 0…1: the values are clamped to it (8.6.5.5)
        internal double[]? Range { get; init; }

        // the 8-bit rows go through the tables as bytes / 255: right without /Range, and for Lab with its usual range (bytes 0…255 decode to
        // L 0…100, a and b −128…127, whose encoding is again bytes / 255)
        internal override bool HasRow8 => (Range is null || source.Space == IccProfile.LabSpace && Range.SequenceEqual([0.0, 100, -128, 127, -128, 127]))
            && (_shaper is not null || transform.HasRow8);

        private IccTransform transform { get; } = transform;

        public override double[] DefaultDecode(int bitsPerComponent) => Range is { } r ? (double[])r.Clone() : base.DefaultDecode(bitsPerComponent);

        private ReadOnlySpan<double> Unit(ReadOnlySpan<double> components, Span<double> unit)
        {
            if (Range is not { } r) return components;
            for (var i = 0; i < unit.Length; i++)
            {
                var v = i < components.Length ? components[i] : r[2 * i];
                v = Math.Clamp(double.IsNaN(v) ? r[2 * i] : v, r[2 * i], r[2 * i + 1]);
                // Lab: L 0…100 and a, b −128…127 onto 0…1 (ICC.1 6.3.4.2); XYZ: 1.0 is 8000h of FFFFh (ICC.1 6.3.4.3); others as they are
                unit[i] = source.Space == IccProfile.LabSpace ? i == 0 ? v / 100 : (v + 128) / 255
                    : source.Space == 0x58595A20 ? v * 32768 / 65535 : v;
            }
            return unit;
        }

        public override int Components => transform.Channels;

        public override bool IsDeviceCmyk => deviceCmyk;

        // with /Range: each component 0 or the nearest value of its range (8.6.8; before, 0 even outside it — No. 1126)
        public override double[] InitialColor => deviceCmyk ? [0, 0, 0, 1]
            : Range is { } r ? [.. Enumerable.Range(0, transform.Channels).Select(i => Math.Clamp(0, r[2 * i], r[2 * i + 1]))] : base.InitialColor;

        public override Rgb ToRgb(ReadOnlySpan<double> components) => transform.ToRgb(Unit(components, stackalloc double[transform.Channels]));

        internal override Rgb ToRgbExact(ReadOnlySpan<double> components) => transform.ToRgbExact(Unit(components, stackalloc double[transform.Channels]));


        internal override void ConvertRow8(ReadOnlySpan<byte> row, int width, Span<byte> rgba)
        {
            if (_shaper is not null) _shaper.Row8(row, width, rgba);
            else transform.Row8(row, width, rgba);
        }

    }

    // ------------------------------------------------------------ CIE-based

    /// <summary>CalGray (8.6.5.2): Y = A^Gamma under its own white point; /BlackPoint and the rendering intent as <see cref="CieMapping"/> says.</summary>
    private sealed class CalGraySpace : ColorSpace
    {
        private readonly PdfDictionary? _dict;
        private readonly float[] _white;
        private readonly float _gamma;
        private readonly CieMapping _mapping;

        public CalGraySpace(PdfDictionary? dict, byte intent = RelativeColorimetric, bool compensate = true)
        {
            _dict = dict;
            _white = WhitePoint(dict);
            _gamma = (float)(dict?.GetNumber(PdfName.Get("Gamma")) ?? 1);
            _mapping = CieMapping.For(_white, BlackPoint(dict), intent, compensate);
        }

        public override int Components => 1;

        private protected override bool DependsOnRendering => true;

        private protected override ColorSpace Rendered(byte intent, bool compensate) =>
            CieMapping.For(_white, BlackPoint(_dict), intent, compensate).Same(_mapping) ? this : new CalGraySpace(_dict, intent, compensate);

        public override Rgb ToRgb(ReadOnlySpan<double> c)
        {
            var y = MathF.Pow(Math.Clamp(c.Length > 0 ? (float)c[0] : 0, 0, 1), _gamma);
            Span<float> xyz = [_white[0] * y, _white[1] * y, _white[2] * y];
            return _mapping.ToRgb(_white, xyz);
        }
    }

    /// <summary>CalRGB (8.6.5.3): XYZ = Matrix · (A^Gr, B^Gg, C^Gb) under its own white point; 8-bit images through tables.</summary>
    private sealed class CalRgbSpace : ColorSpace
    {
        private readonly PdfDictionary? _dict;
        private readonly CieMapping _mapping;
        private readonly MatrixShaper _shaper;

        public CalRgbSpace(PdfDictionary? dict, byte intent = RelativeColorimetric, bool compensate = true)
        {
            _dict = dict;
            var white = WhitePoint(dict);
            _mapping = CieMapping.For(white, BlackPoint(dict), intent, compensate);
            var gamma = Numbers(dict?.GetArray(PdfName.Get("Gamma"))) is { Length: 3 } g ? g : [1.0, 1.0, 1.0];
            // /Matrix: XA YA ZA XB YB ZB XC YC ZC (column by column, per component)
            var given = Numbers(dict?.GetArray(PdfName.Get("Matrix"))) is { Length: 9 } mm ? mm : [1.0, 0, 0, 0, 1, 0, 0, 0, 1];
            var matrix = new float[9];
            for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
                matrix[row * 3 + col] = (float)given[col * 3 + row];
            // fold the linear part of the mapping into the PCS (the Bradford adaptation of the white point to D50 and the factors of the black
            // point, or for absolute colorimetric the XYZ as they are) into the matrix, the offset of the black point into the shaper
            for (var col = 0; col < 3; col++)
            {
                Span<float> v = [matrix[col], matrix[3 + col], matrix[6 + col]];
                _mapping.Linear(white, v);
                (matrix[col], matrix[3 + col], matrix[6 + col]) = (v[0], v[1], v[2]);
            }
            var curves = gamma.Select(x => IccCurve.Gamma((float)x)).ToArray();
            _shaper = new MatrixShaper(curves, matrix, default, _mapping.Offset);
        }

        public override int Components => 3;

        private protected override bool DependsOnRendering => true;

        private protected override ColorSpace Rendered(byte intent, bool compensate) =>
            CieMapping.For(WhitePoint(_dict), BlackPoint(_dict), intent, compensate).Same(_mapping) ? this : new CalRgbSpace(_dict, intent, compensate);

        public override Rgb ToRgb(ReadOnlySpan<double> c) => _shaper.ToRgb(c);

        internal override bool HasRow8 => true;

        internal override void ConvertRow8(ReadOnlySpan<byte> row, int width, Span<byte> rgba) => _shaper.Row8(row, width, rgba);
    }

    private static float[] WhitePoint(PdfDictionary? dict) =>
        Numbers(dict?.GetArray(PdfName.Get("WhitePoint"))) is { Length: 3 } wp && wp[1] > 0
            ? [(float)wp[0], (float)wp[1], (float)wp[2]]
            : [IccProfile.WhiteX, IccProfile.WhiteY, IccProfile.WhiteZ];

    /// <summary>
    /// /BlackPoint of CalGray, CalRGB, Lab (8.6.5.2–8.6.5.4): three numbers, none negative; anything else is passed over as before (the default
    /// [0 0 0]). Null for the default.
    /// </summary>
    private static float[]? BlackPoint(PdfDictionary? dict)
    {
        if (dict?.GetArray(PdfName.Get("BlackPoint")) is not { Count: 3 } array) return null;
        var bp = new float[3];
        for (var i = 0; i < 3; i++)
        {
            if (array.GetNumber(i) is not { } v || !double.IsFinite(v) || v < 0) return null;
            bp[i] = (float)v;
        }
        return bp[0] == 0 && bp[1] == 0 && bp[2] == 0 ? null : bp;
    }

    /// <summary>XYZ under <paramref name="white"/> → sRGB (relative colorimetric: the white point adapted to D50 by Bradford).</summary>
    private static Rgb FromXyz(ReadOnlySpan<float> white, Span<float> xyz)
    {
        IccColor.AdaptToD50(white, xyz);
        Span<float> rgb = stackalloc float[3];
        IccColor.XyzToSrgb(xyz[0], xyz[1], xyz[2], rgb);
        return Rgb.FromUnit(rgb[0], rgb[1], rgb[2]);
    }

    /// <summary>
    /// How the XYZ of a CIE-based space (CalGray, CalRGB, Lab) under its white point enter the PCS (D50, whose white the display shows as its
    /// white) for a rendering intent (docs/FEHLERGEDAECHTNIS.md, No. 1129):
    /// <list type="bullet">
    /// <item>relative colorimetric, perceptual, saturation (no tables, all three alike): the white point adapted to D50 by Bradford, so that it
    /// is the display's white (8.6.5.3 sentences 22–28); with black point compensation (Default, ON) the /BlackPoint, adapted the same way,
    /// is then moved onto the display's black by a linear scaling in XYZ that keeps the white — per component X′ = (X − Xk) · Xw / (Xw − Xk),
    /// as ISO 18619 scales; a black point not darker than the white in every component is passed over;</item>
    /// <item>absolute colorimetric: the XYZ as they are, with no correction for the white point (8.6.5.8, Table 69) and without black point
    /// (8.6.5.9 sentence 6) — a white point other than D50 shows as a tint (D65 bluish); a Y of the white point other than 1.0 (8.6.5.2: shall
    /// be 1.0) is divided out.</item>
    /// </list>
    /// </summary>
    private sealed class CieMapping
    {
        private readonly bool _absolute;
        private readonly float[]? _black;   // the black point in the PCS, where it is compensated

        private CieMapping(bool absolute, float[]? black) => (_absolute, _black) = (absolute, black);

        /// <summary>The offset in linear sRGB that the black point adds (null without one).</summary>
        public float[]? Offset { get; private init; }

        public static CieMapping For(float[] white, float[]? blackPoint, byte intent, bool compensate)
        {
            if (intent == AbsoluteColorimetric) return new CieMapping(true, null);
            if (!compensate || blackPoint is null) return new CieMapping(false, null);
            Span<float> bp = [blackPoint[0], blackPoint[1], blackPoint[2]];
            IccColor.AdaptToD50(white, bp);
            if (!(bp[0] is >= 0 and < IccProfile.WhiteX && bp[1] is >= 0 and < IccProfile.WhiteY && bp[2] is >= 0 and < IccProfile.WhiteZ))
                return new CieMapping(false, null);
            float[] black = [bp[0], bp[1], bp[2]];
            Span<float> offset = stackalloc float[3];
            IccColor.XyzToSrgbLinear(-black[0] * Factor(black, 0), -black[1] * Factor(black, 1), -black[2] * Factor(black, 2), offset);
            return new CieMapping(false, black) { Offset = [offset[0], offset[1], offset[2]] };
        }

        private static float White(int i) => i switch { 0 => IccProfile.WhiteX, 1 => IccProfile.WhiteY, _ => IccProfile.WhiteZ };

        private static float Factor(float[] black, int i) => White(i) / (White(i) - black[i]);

        /// <summary>The same mapping (then a variant would equal its space).</summary>
        public bool Same(CieMapping other) =>
            _absolute == other._absolute && (_black is null ? other._black is null : other._black is not null && _black.AsSpan().SequenceEqual(other._black));

        /// <summary>The linear part (without the offset of the black point), in place: XYZ under <paramref name="white"/> → PCS.</summary>
        public void Linear(ReadOnlySpan<float> white, Span<float> xyz)
        {
            if (_absolute)
            {
                for (var i = 0; i < 3; i++) xyz[i] /= white[1];
                return;
            }
            IccColor.AdaptToD50(white, xyz);
            if (_black is null) return;
            for (var i = 0; i < 3; i++) xyz[i] *= Factor(_black, i);
        }

        /// <summary>XYZ under <paramref name="white"/> → sRGB.</summary>
        public Rgb ToRgb(ReadOnlySpan<float> white, Span<float> xyz)
        {
            if (!_absolute && _black is null) return FromXyz(white, xyz);
            Linear(white, xyz);
            if (_black is not null)
                for (var i = 0; i < 3; i++) xyz[i] -= _black[i] * Factor(_black, i);
            Span<float> rgb = stackalloc float[3];
            IccColor.XyzToSrgb(xyz[0], xyz[1], xyz[2], rgb);
            return Rgb.FromUnit(rgb[0], rgb[1], rgb[2]);
        }
    }

    /// <summary>
    /// Matrix and tone curves (ICC matrix profile, CalRGB): one table per channel for 8 bits, one matrix to linear sRGB,
    /// encoding through a table — accurate and fast, without a grid.
    /// </summary>
    private sealed class MatrixShaper
    {
        private readonly IccCurve[] _curves;
        private readonly float[] _toLinear = new float[9];
        private readonly float _o0, _o1, _o2;
        private readonly float[][] _linear;
        private static readonly byte[] EncodeTable = BuildEncode();

        /// <param name="curves">The tone curves.</param>
        /// <param name="xyzMatrix">Curves → XYZ (D50), row by row.</param>
        /// <param name="pcsScale">Factors applied to X, Y, Z after the matrix (empty: none) — ICC-absolute colorimetry.</param>
        /// <param name="linearOffset">An offset in linear sRGB added after the matrix (null: none) — black point compensation.</param>
        public MatrixShaper(IccCurve[] curves, float[] xyzMatrix, ReadOnlySpan<float> pcsScale = default, float[]? linearOffset = null)
        {
            _curves = curves;
            // XYZ (D50) → linear sRGB, combined with the matrix of the profile and the factors after it
            Span<float> column = stackalloc float[3];
            for (var c = 0; c < 3; c++)
            {
                if (pcsScale.IsEmpty) IccColor.XyzToSrgbLinear(xyzMatrix[c], xyzMatrix[3 + c], xyzMatrix[6 + c], column);
                else IccColor.XyzToSrgbLinear(xyzMatrix[c] * pcsScale[0], xyzMatrix[3 + c] * pcsScale[1], xyzMatrix[6 + c] * pcsScale[2], column);
                _toLinear[c] = column[0];
                _toLinear[3 + c] = column[1];
                _toLinear[6 + c] = column[2];
            }
            if (linearOffset is not null) (_o0, _o1, _o2) = (linearOffset[0], linearOffset[1], linearOffset[2]);
            _linear = new float[3][];
            for (var c = 0; c < 3; c++)
            {
                _linear[c] = new float[256];
                for (var v = 0; v < 256; v++) _linear[c][v] = curves[c].Evaluate(v / 255f);
            }
        }

        /// <summary>The shaper of a matrix profile's transform, with its affine map in the PCS (black point compensation, ICC-absolute).</summary>
        public static MatrixShaper? From(IccTransform transform)
        {
            var profile = transform.Profile;
            if (!profile.IsMatrixShaper || profile.Matrix is not { } m || profile.Curves is not { } c) return null;
            var o = transform.PcsOffset;
            float[]? offset = null;
            if (o[0] != 0 || o[1] != 0 || o[2] != 0)
            {
                Span<float> linear = stackalloc float[3];
                IccColor.XyzToSrgbLinear(o[0], o[1], o[2], linear);
                offset = [linear[0], linear[1], linear[2]];
            }
            var s = transform.PcsScale;
            return new MatrixShaper(c, m, s[0] == 1 && s[1] == 1 && s[2] == 1 ? default : s, offset);
        }

        private static byte[] BuildEncode()
        {
            var table = new byte[4097];
            for (var i = 0; i <= 4096; i++) table[i] = (byte)Math.Clamp((int)Math.Round(IccColor.Encode(i / 4096f) * 255), 0, 255);
            return table;
        }

        public Rgb ToRgb(ReadOnlySpan<double> c)
        {
            if (c.Length < 3) return Rgb.Black;
            float a = _curves[0].Evaluate((float)c[0]), b = _curves[1].Evaluate((float)c[1]), d = _curves[2].Evaluate((float)c[2]);
            var m = _toLinear;
            return Rgb.FromUnit(IccColor.Encode(m[0] * a + m[1] * b + m[2] * d + _o0), IccColor.Encode(m[3] * a + m[4] * b + m[5] * d + _o1),
                IccColor.Encode(m[6] * a + m[7] * b + m[8] * d + _o2));
        }

        public void Row8(ReadOnlySpan<byte> row, int width, Span<byte> rgba)
        {
            var m = _toLinear;
            float[] l0 = _linear[0], l1 = _linear[1], l2 = _linear[2];
            for (int x = 0, s = 0, d = 0; x < width; x++, s += 3, d += 4)
            {
                if (s + 3 > row.Length)
                {
                    rgba[d] = rgba[d + 1] = rgba[d + 2] = 0;
                    rgba[d + 3] = 255;
                    continue;
                }
                float a = l0[row[s]], b = l1[row[s + 1]], c = l2[row[s + 2]];
                rgba[d] = Encode(m[0] * a + m[1] * b + m[2] * c + _o0);
                rgba[d + 1] = Encode(m[3] * a + m[4] * b + m[5] * c + _o1);
                rgba[d + 2] = Encode(m[6] * a + m[7] * b + m[8] * c + _o2);
                rgba[d + 3] = 255;
            }
        }

        private static byte Encode(float linear) => EncodeTable[linear <= 0 ? 0 : linear >= 1 ? 4096 : (int)(linear * 4096 + 0.5f)];
    }
}
