using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Filters.Jpx;

/// <summary>Colour space of the decoded channels, from the Colour Specification boxes of the JP2/JPX wrapper.</summary>
internal enum JpxColor
{
    /// <summary>Not specified, or nothing supported (raw codestream): determined by the number of channels (ISO 32000-2, 7.4.9).</summary>
    Unknown,
    Gray,
    Rgb,
    Cmyk,

    /// <summary>
    /// An ICC profile (<see cref="JpxImage.IccProfile"/>): embedded, or built from the definition of an enumerated colour space that has no
    /// PDF equivalent (ROMM-RGB).
    /// </summary>
    Icc,

    /// <summary>
    /// CIELab (ISO/IEC 15444-2, Table M.25, value 14): the channels already in the encoding of a PDF /Lab colour space with the range
    /// [−128 127 −128 127] — L* 0…100 as 0…255, a* and b* plus 128 — under <see cref="JpxImage.WhitePoint"/>.
    /// </summary>
    Lab,
}

/// <summary>Decoded image: colour channels interleaved at 8 bits, opacity separate.</summary>
internal sealed class JpxImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Channels { get; init; }
    public required byte[] Samples { get; init; }
    public byte[]? Alpha { get; init; }

    /// <summary>The colours are already multiplied by the opacity (channel type 2).</summary>
    public bool PremultipliedAlpha { get; init; }

    public JpxColor Color { get; init; }
    public byte[]? IccProfile { get; init; }

    /// <summary>
    /// The colour channels once more at 16 bits (big-endian, laid out as a PDF image of 16 bits per component) where the image is in the wide
    /// working space (<see cref="JpxParameterized.WideProfile"/>): its 8-bit steps are coarser than those of sRGB, and drawn from 8 bits the
    /// T.803 files 2 and 3 came out up to 7 steps from their reference (Table G.1 allows 4); the display draws from these
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1092). Null otherwise.
    /// </summary>
    public byte[]? Fine { get; init; }

    /// <summary>The white point of <see cref="JpxColor.Lab"/> (CIE XYZ, Y = 1); D50 unless the parameters name another illuminant.</summary>
    public double[] WhitePoint { get; init; } = [0.96422, 1, 0.82521];

    /// <summary>The codestream is incomplete or damaged; what could be decoded is shown.</summary>
    public bool Truncated { get; init; }

    /// <summary>Number of resolution levels by which the image was decoded smaller (width and height halved per level).</summary>
    public int Reduced { get; init; }

    /// <summary>Features the Reader Requirements box asks for to display the image that the decoder lacks (<see cref="JpxRequirements.Missing"/>);
    /// empty without such a box or when nothing is missing.</summary>
    public int[] MissingFeatures { get; init; } = [];

    /// <summary>
    /// The colour space of the channels as a PDF object, for drawing and for writing them into a new image: /ICCBased with the profile,
    /// /Lab with the white point; null for the others, which follow the number of channels (grey, RGB, CMYK).
    /// </summary>
    public PdfObject? SpaceObject()
    {
        switch (Color)
        {
            case JpxColor.Icc when IccProfile is { } icc:
                var stream = new PdfDictionary();
                stream.Set(PdfName.Get("N"), PdfInteger.Of(Channels));
                return new PdfArray([PdfName.Get("ICCBased"), new PdfStream(stream, icc)]);
            case JpxColor.Lab:
                var lab = new PdfDictionary();
                lab.Set(PdfName.Get("WhitePoint"), new PdfArray([.. WhitePoint.Select(v => (PdfObject)new PdfReal(v))]));
                lab.Set(PdfName.Get("Range"), new PdfArray([PdfInteger.Of(-128), PdfInteger.Of(127), PdfInteger.Of(-128), PdfInteger.Of(127)]));
                return new PdfArray([PdfName.Get("Lab"), lab]);
            default:
                return null;
        }
    }

    /// <summary>
    /// The image for a PDF page: where no colour fits its colour channels — not the /ColorSpace (<paramref name="declaredComponents"/>), not
    /// a colour specification of the data, not 1, 3 or 4 channels (ISO 32000-2, 7.4.9 names grey, RGB and CMYK only for those) — the first
    /// channel as grey (two channels) or the first four as CMYK (five and more), the rest passed over; otherwise the image itself. Before all
    /// channels went through RGB, and the image came out in stripes (docs/FEHLERGEDAECHTNIS.md, No. 996).
    /// </summary>
    /// <param name="declaredComponents">The number of components of the image's /ColorSpace; null without one.</param>
    internal JpxImage Fitted(int? declaredComponents)
    {
        if (Channels is not (2 or > 4) || declaredComponents == Channels || Color != JpxColor.Unknown) return this;
        var k = Channels == 2 ? 1 : 4;
        var count = Samples.Length / Channels;
        var samples = new byte[(long)count * k];
        for (long i = 0, s = 0, d = 0; i < count; i++, s += Channels, d += k)
            Array.Copy(Samples, s, samples, d, k);
        return new JpxImage
        {
            Width = Width, Height = Height, Channels = k, Samples = samples, Alpha = Alpha, PremultipliedAlpha = PremultipliedAlpha,
            Color = Color, IccProfile = IccProfile, WhitePoint = WhitePoint, Truncated = Truncated, Reduced = Reduced,
        };
    }
}

/// <summary>
/// JPEG 2000 (JPXDecode, ISO 32000-2, 7.4.9; ISO/IEC 15444-1): raw codestream (J2K) or JP2 wrapper. Large images are
/// decoded at a reduced resolution level until they are below <c>maxPixels</c>; the finer levels are then not
/// decoded at all. Damaged data: what could be read, and <see cref="JpxImage.Truncated"/>.
/// </summary>
internal static class JpxDecoder
{
    /// <summary>Maximum width and height each, and maximum area (as in <see cref="Graphics.PdfImageDecoder"/>).</summary>
    private const int MaxSide = 1 << 20;
    private const long MaxArea = 4_000_000_000;

    /// <summary>Maximum number of colour channels (DeviceN allows up to 32 colourants).</summary>
    private const int MaxChannels = 32;

    /// <summary>Decodes a JPEG 2000 image.</summary>
    /// <param name="data">Codestream or JP2 file.</param>
    /// <param name="maxPixels">Area limit; the image is reduced by resolution levels until it fits.</param>
    /// <param name="applyPalette">Apply a pclr palette; false if the PDF page supplies the palette itself (/Indexed).</param>
    /// <param name="ownColour">
    /// Given the number of colour channels, whether the colour specifications of the data count; false where the image dictionary has a
    /// /ColorSpace that fits them — then that one determines the samples and the specifications are ignored (ISO 32000-2, 7.4.9): stretched
    /// to 8 bits as they are. Null: always.
    /// </param>
    /// <param name="indexDecode">With <paramref name="applyPalette"/> false (/Indexed of the page): the /Decode array of the image, which maps
    /// the raw indices (ISO 32000-2, 7.4.9 with 8.9.5.2); null for none.</param>
    /// <param name="smaskInData">/SMaskInData of the image dictionary (1: opacity, 2: premultiplied opacity in the data; 0: none) — for data
    /// without a channel definition (<see cref="OpacityLast"/>).</param>
    /// <param name="declaredComponents">The number of components of the image's /ColorSpace; null without one.</param>
    /// <returns>The image.</returns>
    /// <exception cref="JpxException">The data contains no usable codestream, or the image is too large.</exception>
    public static JpxImage Decode(byte[] data, long maxPixels, bool applyPalette = true, Func<int, bool>? ownColour = null, double[]? indexDecode = null,
        int smaskInData = 0, int? declaredComponents = null)
    {
        var p = DecodePlanes(data, maxPixels);
        var (view, shown, planeX0, planeY0) = Displayed(p);
        p = shown;
        return Assemble(view, p.Boxes, p.Planes, p.PlaneWidth, p.PlaneHeight, planeX0, planeY0, p.Width, p.Height, p.Reduce,
            applyPalette, ownColour, p.Truncated, p.Budget, indexDecode, p, smaskInData, declaredComponents);
    }

    /// <summary>
    /// The greatest common divisor M of the subsampling factors of all decoded components (ISO/IEC 15444-1, Equation I-1): where every
    /// component is subsampled by a common factor, the image is shown at the size Equation I-2 gives, ⌈Xsiz/M⌉ − ⌈XOsiz/M⌉ by
    /// ⌈Ysiz/M⌉ − ⌈YOsiz/M⌉ (I.5.3.1.1) — before at the size of the image area on the reference grid, M times as wide and high, every sample
    /// repeated (docs/FEHLERGEDAECHTNIS.md, No. 1054).
    /// </summary>
    internal static int CommonSubsampling(JpxCodestream cs)
    {
        var m = 0;
        foreach (var c in cs.Output) m = Gcd(Gcd(m, c.Dx), c.Dy);
        return Math.Max(1, m);

        static int Gcd(int a, int b)
        {
            while (b != 0) (a, b) = (b, a % b);
            return a;
        }
    }

    /// <summary>
    /// The codestream, the planes and their positions as the assembly sees them: on the grid of the shown image (divided by
    /// <see cref="CommonSubsampling"/>), and moved towards the origin so that the assembly, which counts in 32 bits, reaches every pixel — the
    /// reference grid reaches 2^32 − 1 (docs/FEHLERGEDAECHTNIS.md, No. 1051). Every sample keeps its place: see <see cref="Along"/>.
    /// </summary>
    private static (JpxCodestream View, JpxPlanes Planes, int[] PlaneX0, int[] PlaneY0) Displayed(JpxPlanes p)
    {
        var cs = p.Codestream;
        var m = CommonSubsampling(cs);
        var view = cs.Moved(0, 0);
        (view.X0, view.X1, view.Y0, view.Y1) = (JpxTile.CeilDiv(cs.X0, m), JpxTile.CeilDiv(cs.X1, m), JpxTile.CeilDiv(cs.Y0, m), JpxTile.CeilDiv(cs.Y1, m));
        var output = cs.Output.Select(c => c with { Dx = c.Dx / m, Dy = c.Dy / m }).ToArray();
        var empty = Enumerable.Range(0, output.Length).Select(c => p.PlaneWidth[c] == 0 || p.PlaneHeight[c] == 0).ToArray();
        var x = Along(view.X0, p.Width, [.. output.Select(c => c.Dx)], p.PlaneX0, p.PlaneWidth, empty, p.Reduce, p.Budget);
        var y = Along(view.Y0, p.Height, [.. output.Select(c => c.Dy)], p.PlaneY0, p.PlaneHeight, empty, p.Reduce, p.Budget);
        view = view.Moved(x.Shift, y.Shift);
        view.Output = [.. output.Select((c, i) => c with { Dx = x.Factor[i], Dy = y.Factor[i] })];
        if (!x.Map.Any(map => map is not null) && !y.Map.Any(map => map is not null)) return (view, p, x.Start, y.Start);

        // components resampled onto a finer step of the grid (Along): the same sample at every pixel, from a plane of its own
        var planes = (ushort[][])p.Planes.Clone();
        var planeW = (int[])p.PlaneWidth.Clone();
        var planeH = (int[])p.PlaneHeight.Clone();
        var raw = p.Raw.Length == planes.Length ? (int[]?[])p.Raw.Clone() : p.Raw;
        var numbers = p.Numbers.Length == planes.Length ? (float[]?[])p.Numbers.Clone() : p.Numbers;
        for (var c = 0; c < planes.Length; c++)
        {
            int[]? cols = x.Map[c], rows = y.Map[c];
            if (cols is null && rows is null) continue;
            int w = cols?.Length ?? planeW[c], h = rows?.Length ?? planeH[c];
            var size = (long)w * h;
            var withRaw = raw.Length == planes.Length && raw[c] is not null;
            var withNumbers = numbers.Length == planes.Length && numbers[c] is not null;
            var bytes = 2 + (withRaw ? 4 : 0) + (withNumbers ? 4 : 0);
            if (size > Array.MaxLength) throw new JpxException("JPX: Bild zu groß");
            p.Budget.Reserve(size * bytes);
            var source = planes[c];
            var target = new ushort[size];
            var sourceRaw = withRaw ? raw[c]! : null;
            var targetRaw = withRaw ? new int[size] : null;
            var sourceNumbers = withNumbers ? numbers[c]! : null;
            var targetNumbers = withNumbers ? new float[size] : null;
            for (var r = 0; r < h; r++)
            {
                long from = (long)(rows?[r] ?? r) * planeW[c], to = (long)r * w;
                for (var k = 0; k < w; k++)
                {
                    var at = from + (cols?[k] ?? k);
                    target[to + k] = source[at];
                    if (targetRaw is not null) targetRaw[to + k] = sourceRaw![at];
                    if (targetNumbers is not null) targetNumbers[to + k] = sourceNumbers![at];
                }
            }
            p.Budget.Release(source.LongLength * bytes);
            (planes[c], planeW[c], planeH[c]) = (target, w, h);
            if (withRaw) raw[c] = targetRaw;
            if (withNumbers) numbers[c] = targetNumbers;
        }
        return (view, p with { Planes = planes, PlaneWidth = planeW, PlaneHeight = planeH, Raw = raw, Numbers = numbers }, x.Start, y.Start);
    }

    /// <summary>The longest period, on the grid of the reduced image, by which <see cref="Along"/> moves the image towards the origin.</summary>
    private const long MaxPeriod = 1L << 30;

    /// <summary>What <see cref="Along"/> gives the assembly for one direction.</summary>
    /// <param name="Shift">How far the image moves towards the origin, on the grid of the shown image at full resolution.</param>
    /// <param name="Start">The first sample of each plane, counted as the assembly counts it (plane position in samples of <paramref name="Factor"/>).</param>
    /// <param name="Factor">The step of each component on the grid as the assembly sees it: its subsampling factor, or a divisor of it where
    /// the plane is resampled.</param>
    /// <param name="Map">For a resampled plane, the sample of the decoded plane each new sample repeats; null where the plane stays.</param>
    private readonly record struct AxisView(long Shift, int[] Start, int[] Factor, int[]?[] Map);

    /// <summary>
    /// One direction of <see cref="Displayed"/>. The assembly takes, for pixel x of the reduced image, the sample ⌊(x0 + x) / f⌋ − start of a
    /// component with factor f, in 32 bits; the reference grid reaches 2^32 − 1. Moving the image by a multiple D of f towards the origin keeps
    /// that sample; D is chosen as a multiple of the least common multiple L of the factors, as long as L stays within
    /// <see cref="MaxPeriod"/> — then x0 ends below L. Factors that would carry L beyond it (with four or more components their least common
    /// multiple reaches 255 · 254 · 253 · 251, far above 2^31) are left out of L, and where the image has to move all the same, each such
    /// component is resampled onto the step g = gcd(f, L), which divides D: every block of g pixels lies inside one block of f, so the new
    /// plane repeats the very sample the assembly would have taken at each pixel. Before such codestreams were refused as soon as a coordinate
    /// exceeded 2^31 − 1, although T.800 Table A.9 allows the factors 1 to 255 with coordinates up to 2^32 − 1 (docs/FEHLERGEDAECHTNIS.md,
    /// No. 1060). The new planes are at most as long as the shown image in that direction (one more sample); they and the tables of which
    /// sample each repeats count against the memory budget, and components with the same factor share one table.
    /// </summary>
    /// <param name="origin">XOsiz (or YOsiz) on the grid of the shown image.</param>
    /// <param name="size">Width (or height) of the shown image at the reduced resolution.</param>
    /// <param name="factors">The subsampling factor of each component on the grid of the shown image.</param>
    /// <param name="planeStart">The first sample of each decoded plane, ⌈⌈origin / f⌉ / 2^reduce⌉.</param>
    /// <param name="planeSize">The number of samples of each decoded plane in this direction.</param>
    /// <param name="empty">Components without samples: nothing to resample.</param>
    /// <param name="reduce">The number of resolution levels left out.</param>
    /// <param name="budget">The memory budget of the image.</param>
    private static AxisView Along(long origin, int size, int[] factors, long[] planeStart, int[] planeSize, bool[] empty, int reduce, JpxDecodeState budget)
    {
        var n = factors.Length;
        var first = JpxTile.CeilDiv(origin, 1L << reduce);   // x0 of the assembly before moving
        long lcm = 1;
        var inPeriod = new bool[n];
        for (var c = 0; c < n; c++)
        {
            var next = lcm / Gcd(lcm, factors[c]) * factors[c];   // at most 2^30 · 255
            if (next > MaxPeriod && !empty[c]) continue;
            if (next <= MaxPeriod) lcm = next;
            inPeriod[c] = true;
        }
        var all = inPeriod.All(k => k);
        // with every factor in the period the image always moves (as before); otherwise only where the assembly would not reach it
        var moved = all || first + Math.Max(1, size) > int.MaxValue ? (origin >> reduce) / lcm * lcm : 0;
        var x0 = first - moved;   // at most lcm, so x0 + size stays far below 2^31
        if (x0 + Math.Max(1, size) > int.MaxValue) throw new JpxException("JPX: Koordinate zu groß");
        var start = new int[n];
        var factor = new int[n];
        var map = new int[]?[n];
        var shared = new Dictionary<(int Factor, long Start, int Size), int[]>();
        for (var c = 0; c < n; c++)
        {
            if (empty[c])
            {
                (start[c], factor[c]) = (0, factors[c]);
                continue;
            }
            if (inPeriod[c] || moved == 0)
            {
                (start[c], factor[c]) = ((int)(planeStart[c] - moved / factors[c]), factors[c]);
                continue;
            }
            var g = (int)Gcd(lcm, factors[c]);
            var s = x0 / g;
            if (!shared.TryGetValue((factors[c], planeStart[c], planeSize[c]), out var samples))
            {
                var count = (x0 + Math.Max(1, size) - 1) / g - s + 1;   // at most size + 1
                budget.Reserve(count * 4);
                samples = new int[count];
                for (var j = 0; j < count; j++)
                    samples[j] = (int)Math.Clamp(((s + j) * g + moved) / factors[c] - planeStart[c], 0, planeSize[c] - 1);
                shared[(factors[c], planeStart[c], planeSize[c])] = samples;
            }
            (start[c], factor[c], map[c]) = ((int)s, g, samples);
        }
        return new AxisView(moved << reduce, start, factor, map);

        static long Gcd(long a, long b)
        {
            while (b != 0) (a, b) = (b, a % b);
            return a;
        }
    }

    /// <summary>
    /// Whether, in data without a channel definition (a bare codestream cannot carry one), the last of <paramref name="channels"/> channels
    /// is the opacity channel /SMaskInData announces: where the colour that applies fits all channels but the last and not all of them —
    /// /ColorSpace where present, otherwise the colour specification of the data, otherwise grey, RGB or CMYK by 1, 3 or 4 channels
    /// (ISO 32000-2, 7.4.9). Without a channel definition every channel is an ordinary one, and a /ColorSpace must have as many components
    /// as there are (7.4.9): the file does not conform, and the image dictionary says what the extra channel is. Before it was a colour,
    /// and an image of grey and opacity came out in stripes (docs/FEHLERGEDAECHTNIS.md, No. 996).
    /// </summary>
    /// <param name="channels">The number of channels.</param>
    /// <param name="declaredComponents">The number of components of the image's /ColorSpace; null without one.</param>
    /// <param name="specs">The colour specifications of the data, best first (none for a bare codestream).</param>
    internal static bool OpacityLast(int channels, int? declaredComponents, IReadOnlyList<JpxColourSpec> specs)
    {
        if (channels < 2 || declaredComponents == channels) return false;
        if (declaredComponents == channels - 1) return true;
        // a /ColorSpace that fits neither is ignored when drawing (PdfImageDecoder.JpxSpace): the data decide, as without one
        if (Choose(specs, channels).Color != JpxColor.Unknown) return false;
        if (Choose(specs, channels - 1).Color != JpxColor.Unknown) return true;
        return channels is not (1 or 3 or 4) && channels - 1 is 1 or 3 or 4;
    }

    /// <summary>
    /// The memory decoding at full size needs, from the header alone (the planes of all components at 16 bits and the 8-bit result) — to
    /// decide before allocating whether it fits.
    /// </summary>
    /// <param name="data">Codestream or JP2 file.</param>
    /// <returns>Bytes; null if the header cannot be read.</returns>
    internal static long? FullSizeBytes(byte[] data)
    {
        try
        {
            var box = JpxBoxes.Read(data);
            var cs = JpxCodestream.Parse(box.Data, box.Start, box.End);
            // checked: width and height reach 2^32 − 1 each (an overflow counts as a header that cannot be used)
            return checked(cs.Width * cs.Height * Math.Max(1, Math.Max(cs.Components.Length, cs.Output.Length)) * 3);
        }
        catch (Exception e) when (e is JpxException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Decodes the components separately, raw values after the level shift (for comparison with reference images, tests).</summary>
    /// <param name="data">Codestream or JP2 file.</param>
    /// <param name="maxPixels">Area limit used to choose the reduction.</param>
    /// <param name="fixedReduce">Reduce by exactly this many levels; if not given, according to <paramref name="maxPixels"/>.</param>
    internal static JpxPlanes DecodePlanes(byte[] data, long maxPixels, int? fixedReduce = null)
    {
        var box = JpxBoxes.Read(data);
        var cs = JpxCodestream.Parse(box.Data, box.Start, box.End);
        long width = cs.Width, height = cs.Height;
        if (width > MaxSide || height > MaxSide || width * height > MaxArea) throw new JpxException("JPX: Bild zu groß");

        // Reduce until it fits (at most as far as the decomposition levels allow)
        var minLevels = cs.Main.Coding!.Levels;
        foreach (var coc in cs.Main.ComponentCoding)
            if (coc is not null) minLevels = Math.Min(minLevels, coc.Levels);
        var reduce = fixedReduce is { } r ? Math.Clamp(r, 0, minLevels) : 0;
        while (fixedReduce is null && reduce < minLevels && Area(cs, reduce) > Math.Max(1, maxPixels)) reduce++;
        // If the levels are not enough (huge image without decomposition): do not allocate it at full size
        if (fixedReduce is null && maxPixels < long.MaxValue / 4 && Area(cs, reduce) > 4 * Math.Max(1, maxPixels))
            throw new JpxException("JPX: Bild zu groß");

        // the size of the shown image (Equation I-2, with the common subsampling factor M)
        var m = CommonSubsampling(cs);
        var outW = (int)(JpxTile.CeilDiv(cs.X1, (long)m << reduce) - JpxTile.CeilDiv(cs.X0, (long)m << reduce));
        var outH = (int)(JpxTile.CeilDiv(cs.Y1, (long)m << reduce) - JpxTile.CeilDiv(cs.Y0, (long)m << reduce));
        var pixels = (long)outW * outH;
        // Work: a generous 400 coefficient passes per pixel and component; memory: up to 16 bytes per pixel for at most
        // 5 components (colour and opacity); more components only while the image is small
        var components = Math.Min(Math.Max(cs.Components.Length, cs.Output.Length), 5);
        var state = new JpxDecodeState(
            workBudget: Math.Max(50_000_000, 400 * pixels * components),
            // the image's own need, within the budget of the machine (No. 743)
            memoryBudget: Math.Min(Math.Max(64L << 20, 16 * pixels * components), PdfLimits.Budget(8L << 30)),
            blockBudget: 1 << 24);

        // Decoded components at output size (after the multiple component transformation of ISO/IEC 15444-2, if any), raw values after the
        // level shift (up to 16 bits)
        var planes = new ushort[cs.Output.Length][];
        var planeW = new int[planes.Length];
        var planeH = new int[planes.Length];
        var planeX0 = new long[planes.Length];
        var planeY0 = new long[planes.Length];
        for (var c = 0; c < planes.Length; c++)
        {
            var comp = cs.Output[c];
            planeX0[c] = JpxTile.CeilDiv(JpxTile.CeilDiv(cs.X0, comp.Dx), 1L << reduce);
            planeY0[c] = JpxTile.CeilDiv(JpxTile.CeilDiv(cs.Y0, comp.Dy), 1L << reduce);
            planeW[c] = (int)(JpxTile.CeilDiv(JpxTile.CeilDiv(cs.X1, comp.Dx), 1L << reduce) - planeX0[c]);
            planeH[c] = (int)(JpxTile.CeilDiv(JpxTile.CeilDiv(cs.Y1, comp.Dy), 1L << reduce) - planeY0[c]);
            var size = (long)planeW[c] * planeH[c];
            if (size > Array.MaxLength) throw new JpxException("JPX: Bild zu groß");
            state.Reserve(size * 2);
            planes[c] = new ushort[size];
        }

        // The Pixel Format box (ISO/IEC 15444-2, M.11.7.8) names channels: a component directly, or a palette column through the Component
        // Mapping box. Fixed and floating point components are turned into levels while the tiles are stored; mantissa and exponent
        // components keep their integers for the assembly, which puts each pair together (docs/FEHLERGEDAECHTNIS.md, No. 969)
        var formats = new int[planes.Length];
        var paletteFormats = new Dictionary<int, int>();
        var raw = new int[planes.Length][];
        var numbers = new float[planes.Length][];
        if (box.PixelFormats is { } named)
        {
            foreach (var (channel, format) in named)
            {
                if (box.Palette is not null && box.Mapping is { Count: > 0 } mapping)
                {
                    if (channel >= mapping.Count) continue;
                    var (component, type, column) = mapping[channel];
                    if (type == 1)
                    {
                        if (JpxPixelFormat.Kind(format) is JpxPixelFormat.Mantissa or JpxPixelFormat.Exponent)
                            throw new JpxException("JPX: Mantisse und Exponent aus einer Palette werden nicht unterstützt");
                        paletteFormats[column] = format;
                    }
                    else if (component < formats.Length) formats[component] = format;
                }
                else if (channel < formats.Length) formats[channel] = format;
            }
            for (var c = 0; c < planes.Length; c++)
            {
                if (formats[c] == 0) continue;
                if (!JpxPixelFormat.Fits(formats[c], cs.Output[c].Precision)) throw new JpxException("JPX: Pixelformat passt nicht zur Bittiefe");
                if (JpxPixelFormat.Kind(formats[c]) is JpxPixelFormat.Mantissa or JpxPixelFormat.Exponent)
                {
                    state.Reserve(planes[c].LongLength * 4);
                    raw[c] = new int[planes[c].Length];
                }
                else if (JpxPixelFormat.IsNumber(formats[c]) && KeepsNumbers(box))
                {
                    state.Reserve(planes[c].LongLength * 4);
                    numbers[c] = new float[planes[c].Length];
                }
            }
        }

        for (var t = 0; t < cs.Tiles.Length; t++)
        {
            if (cs.Tiles[t] is not { } tileData) continue;
            if (state.OverBudget) break;
            var tile = new JpxTile(cs, t, tileData, reduce, state);
            try
            {
                tile.Decode();
            }
            catch (Exception e) when (e is JpxException or IndexOutOfRangeException or ArgumentException or OverflowException)
            {
                state.Truncated = true;
                if (tile.Components.Length == 0 || tile.Components.Any(tc => tc is null)) continue;
            }
            Store(cs, tile, planes, planeW, planeH, planeX0, planeY0, reduce, formats, raw, numbers);
            foreach (var tc in tile.Components) state.Release(tc.Bytes);
        }

        // a channel of the component mapping that names a component the codestream does not have (I.5.3.5, CMPi): Assemble takes the last one,
        // as before, and the image counts as damaged (docs/FEHLERGEDAECHTNIS.md, No. 1050)
        if (box.Mapping is { } named2 && named2.Any(m2 => m2.Component >= cs.Output.Length)) box.Damaged = true;
        // a codestream whose Rsiz names profile 0 or 1 (T.800 Table A.10) but which breaks a restriction Table A.45 sets for it: decoded as any
        // codestream, and reported (decided on 23.11.2026: read and report; before it was read silently, docs/FEHLERGEDAECHTNIS.md, No. 1077).
        // The check reads the marker segments once more, the packet data not at all; other codestreams are not walked
        var profileBroken = cs.Rsiz is 1 or 2 && JpxProfile.Check(box.Data.AsSpan(box.Start, box.End - box.Start)) == JpxProfileResult.Broken;
        return new JpxPlanes(cs, box, planes, planeW, planeH, planeX0, planeY0, outW, outH, reduce,
            state.Truncated || cs.Truncated || box.Damaged || profileBroken)
        {
            Budget = state, Formats = formats, PaletteFormats = paletteFormats, Raw = raw, Numbers = numbers,
        };
    }

    /// <summary>The largest component depth of ISO/IEC 15444-1, Table A.11 (Ssiz 0x25); deeper values of SIZ are reserved.</summary>
    private const int MaxDepth = JpxCodestream.MaxDepth;

    /// <summary>The area of the shown image (Equation I-2) at <paramref name="reduce"/> levels less.</summary>
    private static long Area(JpxCodestream cs, int reduce)
    {
        var step = (long)CommonSubsampling(cs) << reduce;
        return (JpxTile.CeilDiv(cs.X1, step) - JpxTile.CeilDiv(cs.X0, step)) * (JpxTile.CeilDiv(cs.Y1, step) - JpxTile.CeilDiv(cs.Y0, step));
    }

    /// <summary>
    /// Component transformation, level shift, non-linearity, clamping; then copies the tile to its place in the image. With the array-based
    /// multiple component transformation of ISO/IEC 15444-2 (Annex J) the reconstructed components come from <see cref="JpxPart2.Transform"/>
    /// and no level shift applies (its offsets take the place of it, J.1); a non-linearity of Annex K follows on the reconstructed values.
    /// </summary>
    private static void Store(JpxCodestream cs, JpxTile tile, ushort[][] planes, int[] planeW, int[] planeH, long[] planeX0, long[] planeY0, int reduce,
        int[] formats, int[]?[] raw, float[]?[] numbers)
    {
        var tcs = tile.Components;
        var own = tile.OwnHeader.Part2;
        // a wavelet-based multiple component transformation in the COD of a tile-part header is refused as in the main header (SetOutput) —
        // before the tile was decoded the ordinary way, level-shifted and without a transformation: a wrong image without a word (M2, T.801 J.3.2,
        // 23.11.2026)
        if (JpxPart2.MultipleComponent(cs.Rsiz) && (tile.ComponentTransform & 4) != 0)
            throw new JpxException("JPX: Wavelet-Komponenten-Transformation nicht unterstützt");
        if (JpxPart2.MultipleComponent(cs.Rsiz) && (tile.ComponentTransform & 2) != 0 && (own.Order ?? cs.Main.Part2.Order) is { Length: > 0 })
        {
            var first = tcs[0].Resolutions[tcs[0].Keep];
            var samples = first.Width * first.Height;
            for (var c = 0; c < tcs.Length; c++)
            {
                var r = tcs[c].Resolutions[tcs[c].Keep];
                if (r.Width != first.Width || r.Height != first.Height || tcs[c].Length < samples) throw new JpxException("JPX: Komponenten verschieden groß");
            }
            // one component with the exact store: all of them in double precision, so its integers above 2^24 reach the decorrelation unrounded
            if (tcs.Any(tc => tc.Exact is not null))
            {
                foreach (var tc in tcs) tc.Widen(tile.State);
                var wide = tcs.Select(tc => tc.Exact!.Length == samples ? tc.Exact : tc.Exact[..samples]).ToArray();
                var exactOutputs = JpxPart2.TransformExact(own, cs.Main.Part2, wide, cs.Output.Length, tile.State);
                for (var c = 0; c < exactOutputs.Length; c++)
                    Put(c, [], exactOutputs[c], first, tcs[0], cs.Reconstructed[c], shift: 0);
                return;
            }
            var inputs = new float[tcs.Length][];
            for (var c = 0; c < tcs.Length; c++)
                inputs[c] = tcs[c].Data.Length == samples ? tcs[c].Data : tcs[c].Data[..samples];
            var outputs = JpxPart2.Transform(own, cs.Main.Part2, inputs, cs.Output.Length, tile.State);
            for (var c = 0; c < outputs.Length; c++)
                Put(c, outputs[c], null, first, tcs[0], cs.Reconstructed[c], shift: 0);
            return;
        }
        if (tile.MultipleComponentTransform && tcs.Length >= 3)
        {
            var a = tcs[0].Resolutions[tcs[0].Keep];
            var b = tcs[1].Resolutions[tcs[1].Keep];
            var d = tcs[2].Resolutions[tcs[2].Keep];
            if (a.Width == b.Width && a.Width == d.Width && a.Height == b.Height && a.Height == d.Height)
            {
                // one of the three with the exact store: the transform in double precision for all three (the RCT exact, G.2)
                if (tcs[0].Exact is not null || tcs[1].Exact is not null || tcs[2].Exact is not null)
                {
                    for (var c = 0; c < 3; c++) tcs[c].Widen(tile.State);
                    JpxWavelet.InverseColor(tcs[0].Exact!, tcs[1].Exact!, tcs[2].Exact!, a.Width * a.Height, tcs[0].Coding.Reversible);
                }
                else JpxWavelet.InverseColor(tcs[0].Data, tcs[1].Data, tcs[2].Data, a.Width * a.Height, tcs[0].Coding.Reversible);
            }
        }
        for (var c = 0; c < tcs.Length && c < planes.Length; c++)
        {
            var comp = cs.Components[c];
            // the DC level shift of unsigned components (ISO/IEC 15444-1, G.1.2): 2^(depth − 1) for every depth of Table A.11, up to 2^37 — before it was
            // capped at 2^29, and components of 31 to 38 bits came out far too dark (docs/FEHLERGEDAECHTNIS.md, No. 1046)
            Put(c, tcs[c].Data, tcs[c].Exact, tcs[c].Resolutions[tcs[c].Keep], tcs[c], comp,
                shift: comp.Signed ? 0 : 1L << (Math.Min(comp.Precision, MaxDepth) - 1));
        }

        // reconstructed component c (bit depth and sign of its input) → its plane; signed values are shown shifted by half the range. The
        // samples come from the single-precision store, or from the exact one where it is given.
        void Put(int c, float[] data, double[]? exact, JpxResolution res, JpxTileComponent geometry, JpxComponent input, long shift)
        {
            var output = cs.Output[c];
            var nlt = JpxPart2.Extended(cs.Rsiz) ? JpxPart2.NonLinearityOf(tile.OwnHeader.Part2, cs.Main.Part2, c) : null;
            // more table values than the input has values (ISO/IEC 15444-2, K.2.2: at most 2^b_y): computed as given, and reported
            // (docs/FEHLERGEDAECHTNIS.md, No. 1049)
            if (nlt?.Table is { } lut && lut.Length - 1 >> Math.Min(input.Precision, 30) != 0) tile.State.Truncated = true;
            // all depths of Table A.11 in 64 bits: before the range was capped at 30 bits (docs/FEHLERGEDAECHTNIS.md, No. 1046)
            var precision = Math.Min(output.Precision, MaxDepth);
            var max = (1L << precision) - 1;
            var display = output.Signed ? 1L << (precision - 1) : 0;
            // at most the 38 bits of Table A.11: a reserved depth of SIZ (up to 128) ran the shifts below over (probe of T.801, B17)
            var down = Math.Max(0, precision - 16);
            // the reconstructed values within their own range, before a non-linearity
            var inBits = Math.Min(input.Precision, MaxDepth);
            long low = input.Signed ? -(1L << (inBits - 1)) : 0, high = input.Signed ? (1L << (inBits - 1)) - 1 : (1L << inBits) - 1;
            // Fewer levels than requested: this tile comes out larger and is subsampled
            var extra = reduce - (geometry.Coding.Levels - geometry.Keep);
            var plane = planes[c];
            var format = formats[c];
            var rawPlane = raw[c];
            var numberPlane = numbers[c];
            // a fixed or floating point channel: its integer within the range of its bit depth, read as that number, as a level of up to 16
            // bits (what Fill expects for this component); a signed one keeps its negative half, in 16 bits shifted by half the range like a
            // signed integer channel (JpxPixelFormat.SignedLevel; docs/FEHLERGEDAECHTNIS.md, No. 1067)
            var bits = precision;
            long outLow = output.Signed ? -(1L << (bits - 1)) : 0, outHigh = output.Signed ? (1L << (bits - 1)) - 1 : (1L << bits) - 1;
            var levels = (1 << Math.Min(bits, 16)) - 1;
            var signedNumber = output.Signed && JpxPixelFormat.IsNumber(format);
            int pw = planeW[c], ph = planeH[c];
            var y0 = planeY0[c];
            var x0 = planeX0[c];
            Rows(res.Height, (long)res.Width * res.Height, y =>
            {
                var py = ((res.Y0 + y) >> extra) - y0;
                if ((ulong)py >= (ulong)ph) return;
                var src = y * res.Width;
                var dst = (int)py * pw;
                for (var x = 0; x < res.Width; x++)
                {
                    var lx = ((res.X0 + x) >> extra) - x0;
                    if ((ulong)lx >= (ulong)pw) continue;
                    var px = (int)lx;
                    long v = (exact is not null ? (long)Math.Round(exact[src + x]) : (long)MathF.Round(data[src + x])) + shift;
                    if (nlt is not null)
                        v = (long)Math.Round(JpxPart2.ToDepth(JpxPart2.Apply(nlt, Math.Clamp(v, low, high), input.Precision, input.Signed),
                            nlt.Bits, nlt.Signed, output.Precision, output.Signed));
                    if (format != 0)
                    {
                        v = Math.Clamp(v, outLow, outHigh);
                        // scRGB: the number as it is, for the assembly (KeepsNumbers); the level below for every other colour
                        if (numberPlane is not null) numberPlane[dst + px] = (float)JpxPixelFormat.Value(v, bits, format);
                        if (rawPlane is not null) rawPlane[dst + px] = (int)v;
                        else if (signedNumber) plane[dst + px] = JpxPixelFormat.SignedLevel(JpxPixelFormat.Value(v, bits, format));
                        else plane[dst + px] = JpxPixelFormat.Level(JpxPixelFormat.Value(v, bits, format), levels);
                        continue;
                    }
                    v += display;
                    v = v < 0 ? 0 : v > max ? max : v;
                    plane[dst + px] = (ushort)(v >> down);
                }
            });
        }
    }

    private static JpxImage Assemble(JpxCodestream cs, JpxBoxes box, ushort[][] planes, int[] planeW, int[] planeH, int[] planeX0, int[] planeY0,
        int width, int height, int reduce, bool applyPalette, Func<int, bool>? ownColour, bool truncated, JpxDecodeState budget, double[]? indexDecode = null,
        JpxPlanes? decoded = null, int smaskInData = 0, int? declaredComponents = null)
    {
        var formats = decoded?.Formats is { Length: > 0 } f ? f : new int[planes.Length];
        // the bits of each plane: up to 16; a signed fixed or floating point component came as a 16-bit level (Store)
        var precision = cs.Output.Select((c, i) =>
            i < formats.Length && JpxPixelFormat.IsNumber(formats[i]) ? JpxPixelFormat.LevelBits(c.Precision, c.Signed) : Math.Min(16, c.Precision)).ToArray();
        var raw = decoded?.Raw is { Length: > 0 } r ? r : new int[planes.Length][];
        var numbers = decoded?.Numbers is { Length: > 0 } nb && nb.Length == planes.Length ? (float[]?[])nb.Clone() : new float[planes.Length][];
        var paletteFormats = decoded?.PaletteFormats ?? [];
        var count = (long)width * height;
        var x0 = (int)JpxTile.CeilDiv(cs.X0, 1L << reduce);
        var y0 = (int)JpxTile.CeilDiv(cs.Y0, 1L << reduce);

        // Sample of a component at an image pixel (subsampling: nearest neighbour). Column tables only for components that are drawn,
        // and identical tables shared: one full image row per component would take 20 GB for a crafted file (8000 × 1 million)
        var shared = new Dictionary<(int Dx, int X0, int W), int[]>();
        int[] Columns(int c)
        {
            var dx = cs.Output[c].Dx;
            if (shared.TryGetValue((dx, planeX0[c], planeW[c]), out var cols)) return cols;
            budget.Reserve(width * 4L);
            cols = new int[width];
            for (var x = 0; x < width; x++) cols[x] = Math.Clamp((x0 + x) / dx - planeX0[c], 0, Math.Max(0, planeW[c] - 1));
            return shared[(dx, planeX0[c], planeW[c])] = cols;
        }
        int Row(int c, int y) => Math.Clamp((y0 + y) / cs.Output[c].Dy - planeY0[c], 0, Math.Max(0, planeH[c] - 1));

        // Channels: a component directly or via the palette (cmap/pclr)
        var channels = new List<(int Component, int PaletteColumn)>();
        if (box.Palette is { } palette && box.Mapping is { Count: > 0 } mapping && applyPalette)
        {
            foreach (var (component, type, column) in mapping)
                channels.Add((Math.Min(component, planes.Length - 1), type == 1 && column < palette.Columns ? column : -1));
        }
        else if (box.Mapping is { Count: > 0 } direct && !applyPalette)
        {
            // The PDF page supplies the palette itself (/Indexed): keep the indices unchanged
            channels.Add((Math.Min(direct[0].Component, planes.Length - 1), -1));
        }
        else
        {
            for (var c = 0; c < planes.Length; c++) channels.Add((c, -1));
        }

        // Channel definition: colours sorted by association, opacity separate. The Opacity box of the first compositing layer (ISO/IEC 15444-2,
        // M.11.7.6) says the same in short: the last channel is opacity (type 0) or premultiplied opacity (type 1), the others are colours in
        // their order; a chroma key (type 2) is ignored in PDF (ISO 32000-2, 7.4.9)
        var colorChannels = Enumerable.Range(0, channels.Count).ToList();
        int alphaChannel = -1;
        var premultiplied = false;
        List<(int Channel, int Type, int Association)>? definitions = box.OpacityType is 0 or 1 && channels.Count >= 2
            ? [.. Enumerable.Range(0, channels.Count - 1).Select(i => (i, 0, i + 1)), (channels.Count - 1, box.OpacityType.Value + 1, 0)]
            : box.Definitions;
        // no channel definition, /SMaskInData and one channel more than the colour: the last is the opacity the image dictionary announces,
        // for all colour channels (ISO 32000-2, 7.4.9; docs/FEHLERGEDAECHTNIS.md, No. 996)
        if (definitions is not { Count: > 0 } && smaskInData is 1 or 2 && applyPalette && OpacityLast(channels.Count, declaredComponents, box.Colours))
            definitions = [.. Enumerable.Range(0, channels.Count - 1).Select(i => (i, 0, i + 1)), (channels.Count - 1, smaskInData, 0)];
        if (definitions is { Count: > 0 } defs)
        {
            var colors = new List<(int Channel, int Association)>();
            foreach (var (channel, type, association) in defs)
            {
                if (channel >= channels.Count) continue;
                if (type == 0) colors.Add((channel, association == 0 ? int.MaxValue : association));
                else if (type is 1 or 2 && alphaChannel < 0)
                {
                    alphaChannel = channel;
                    premultiplied = type == 2;
                }
            }
            if (colors.Count > 0) colorChannels = [.. colors.OrderBy(c => c.Association).Select(c => c.Channel)];
            else colorChannels.Remove(alphaChannel);
        }

        // Mantissa and exponent channels (ISO/IEC 15444-2, M.11.7.8, Table M.35bis): each mantissa channel takes the exponent channel of
        // its colour, else the one for the whole image (association 0), as the Channel Definition box associates them; the number
        // V = mantissa · 2^(exponent − 136) becomes the level of the mantissa channel, and exponent channels are no colours — their type
        // in the Channel Definition box is ignored (docs/FEHLERGEDAECHTNIS.md, No. 969)
        if (raw.Any(p => p is not null))
        {
            int KindOf(int channel) => channels[channel].PaletteColumn < 0 ? JpxPixelFormat.Kind(formats[channels[channel].Component]) : 0;
            if (definitions is not { Count: > 0 }) throw new JpxException("JPX: Mantisse und Exponent ohne Kanaldefinition");
            var association = definitions.Where(d => d.Channel < channels.Count).GroupBy(d => d.Channel).ToDictionary(g => g.Key, g => g.First().Association);
            var exponents = Enumerable.Range(0, channels.Count).Where(ch => KindOf(ch) == JpxPixelFormat.Exponent).ToList();
            foreach (var ch in Enumerable.Range(0, channels.Count).Where(ch => KindOf(ch) == JpxPixelFormat.Mantissa))
            {
                var own = association.GetValueOrDefault(ch, -1);
                var e = exponents.Where(x => association.GetValueOrDefault(x, -1) == own).Concat(exponents.Where(x => association.GetValueOrDefault(x, -1) == 0))
                    .Cast<int?>().FirstOrDefault() ?? throw new JpxException("JPX: Mantisse ohne Exponent");
                int mc = channels[ch].Component, ec = channels[e].Component;
                if (planes[mc].Length != planes[ec].Length || planeW[mc] != planeW[ec]) throw new JpxException("JPX: Mantisse und Exponent verschieden groß");
                var mantissa = raw[mc]!;
                var exponent = raw[ec]!;
                var plane = planes[mc];
                for (var i = 0; i < plane.Length; i++) plane[i] = JpxPixelFormat.Level(JpxPixelFormat.Combined(mantissa[i], exponent[i]), 65535);
                precision[mc] = 16;
                // scRGB: the number as it is as well (KeepsNumbers)
                if (KeepsNumbers(box) && numbers[mc] is null)
                {
                    budget.Reserve(plane.LongLength * 4);
                    var kept = new float[plane.Length];
                    for (var i = 0; i < kept.Length; i++) kept[i] = (float)JpxPixelFormat.Combined(mantissa[i], exponent[i]);
                    numbers[mc] = kept;
                }
            }
            colorChannels.RemoveAll(ch => KindOf(ch) == JpxPixelFormat.Exponent);
            if (alphaChannel >= 0 && KindOf(alphaChannel) == JpxPixelFormat.Exponent) alphaChannel = -1;
        }

        // Assembly uses the same memory budget as decoding; the channel count from cmap/cdef is limited (crafted files:
        // thousands of channels, up to 2 GB in one array); without any colour channel (opacity only) there is nothing to draw
        var n = colorChannels.Count;
        if (n is 0 or > MaxChannels) throw new JpxException("JPX: Kanäle");
        if (count * n > Array.MaxLength) throw new JpxException("JPX: Bild zu groß");
        budget.Reserve(count * n + (alphaChannel >= 0 ? count : 0));

        // Colour: the supported specification of the highest precedence (ISO 32000-2, 7.4.9; ISO/IEC 15444-2, M.11.7.2) — an ICC profile that
        // cannot be used, or a colour space the colour channels do not fit, gives way to the next; none: by the number of channels. With
        // /Indexed from the PDF page the indices stay as they are and the colour of the page applies, and so does a /ColorSpace of the page
        // that fits the channels (ISO 32000-2, 7.4.9).
        // how each colour channel is a number (Table M.24bis): signed or not — a component by Ssiz, a palette column by Bi, both alike (No. 1070)
        // —, an integer or a fixed or floating point number, and its bit depth; the colour specifications map signed channels from them
        var forms = colorChannels.Select(ch =>
        {
            var (c, column) = channels[ch];
            if (column >= 0)
                return new JpxChannelForm(box.Palette!.Signed(column), JpxPixelFormat.IsNumber(paletteFormats.GetValueOrDefault(column)),
                    box.Palette.Depth(column));
            // a mantissa put together with its exponent is a number of 0 … 1 as an unsigned level of 16 bits (above)
            if (raw.ElementAtOrDefault(c) is not null) return new JpxChannelForm(false, true, 16);
            return new JpxChannelForm(cs.Output[c].Signed, JpxPixelFormat.IsNumber(formats[c]), Math.Min(cs.Output[c].Precision, MaxDepth));
        }).ToArray();
        var colourOfTheFile = applyPalette && (ownColour?.Invoke(n) ?? true);
        var choice = colourOfTheFile ? Choose(box.Colours, n, forms) : default;
        // signed channels through an ICC profile (ISO/IEC 15444-1, I.5.3.3 sentences 36 and 37): clipped to the range of the profile's curve
        // before it, negative values to 0 — not shifted by half the range as for display (decided on 20.11.2026; before they were
        // shifted, docs/FEHLERGEDAECHTNIS.md, No. 979); a signed fixed or floating point channel the same with MAX 1.0 (No. 1067)
        if (choice.Color == JpxColor.Icc && choice.Parameterized is null && forms.Any(s => s.Signed))
        {
            var inner = choice.Map;
            choice.Map = (k, value, bits, down) =>
            {
                if (k >= forms.Length || !forms[k].Signed) return inner?.Invoke(k, value, bits, down) ?? Stretch(value, bits);
                var max = Math.ScaleB(1.0, bits - 1) - 1;
                return (byte)Math.Round(Math.Clamp(value - Math.ScaleB(1.0, bits - 1), 0, max) * 255 / Math.Max(1, max));
            };
        }
        var samples = new byte[count * n];
        byte[]? fine = null;
        // colours beyond the primaries kept in the wide working space where the image has any (docs/FEHLERGEDAECHTNIS.md, No. 1056)
        if (choice.Parameterized is { } parameterized)
        {
            if (FillParameterized(parameterized)) (choice.Color, choice.Icc) = parameterized.Space(n, true);
        }
        else
            for (var k = 0; k < n; k++)
            {
                var map = choice.Map;
                var channel = k;
                Fill(samples, k, n, channels[colorChannels[k]], map is null ? null : (value, bits, down) => map(channel, value, bits, down));
            }
        byte[]? alpha = null;
        if (alphaChannel >= 0)
        {
            alpha = new byte[count];
            Fill(alpha, 0, 1, channels[alphaChannel], null);
        }

        var color = choice.Color;

        return new JpxImage
        {
            Width = width,
            Height = height,
            Channels = n,
            Samples = samples,
            Alpha = alpha,
            PremultipliedAlpha = premultiplied,
            Color = color,
            IccProfile = choice.Icc,
            Fine = fine,
            WhitePoint = choice.White ?? Illuminant(0x00443530),
            Truncated = truncated,
            Reduced = reduce,
            // scRGB (Table M.14, value 83) is provided where the image is drawn as scRGB: the box evaluated again with it (JpxRequirements.ScRgb).
            // Where the PDF gives the colour space (ISO 32000-2, 7.4.9: /ColorSpace, or an indexed space), the colour specification of the file
            // does not count, and neither do the colour spaces its Reader Requirements name — before they were reported as missing all the same
            MissingFeatures = JpxRequirements.WithoutColourSpaces(choice.Parameterized is { IsScRgb: true } && box.Requirements is { } rreq
                ? JpxRequirements.Missing(rreq, 0, rreq.Length, scRgbDrawn: true)
                : box.MissingFeatures, colourOfTheFile),
        };

        // The Parameterized colour specification (ISO/IEC 15444-2, M.11.7.3.4; ITU-T H.273) and the enumerated spaces read the same way (sYCC,
        // e-sYCC, YCbCr, e-sRGB, YPbPr, YCCK): the colour channels of a pixel together, from their codes at full bit depth (a component above
        // 16 bits came shifted down: shifted back up; a palette column with its depth Bi) in double precision to 8 bits — no 8-bit step in
        // between (docs/FEHLERGEDAECHTNIS.md, No. 1044, 1057 and 1072). Signed channels arrive shifted by half their range (Store, JpxPalette)
        // and are mapped from their value of no light (JpxParameterized.FromZero; No. 1066; the Parameterized method too since No. 1076 — before
        // its signed components were read as offset binary codes; YCCK, without a unique value of no light, keeps them shifted). Where colours
        // may lie beyond the primaries, a first pass looks for one that does — only then is the image written in the wide working space, so
        // that an image without such colours stays as before (No. 1056); written so, it gets its colours at 16 bits as well (Fine; No. 1092).
        // True: written so.
        bool FillParameterized(JpxParameterized parameterized)
        {
            var sources = new (ushort[] Plane, int[] Cols, int Component, double[]? Palette, int Shift, float[]? Numbers)[n];
            var bits = new int[n];
            var fromZero = new bool[n];
            for (var k = 0; k < n; k++)
            {
                var (c, column) = channels[colorChannels[k]];
                // scRGB: a fixed or floating point channel is linear light itself, given to it as the 16-bit code of that light
                // (JpxParameterized.NumberCode) — from the number as it is (Numbers), not from the level of 0 … 1 the other colours read
                var number = parameterized.ReadsNumbers && forms[k].Number;
                if (column >= 0)
                {
                    var format = paletteFormats.GetValueOrDefault(column);
                    if (number)
                    {
                        sources[k] = (planes[c], Columns(c), c, [.. box.Palette!.Numbers(column, format).Select(JpxParameterized.NumberCode)], 0, null);
                        bits[k] = 16;
                        continue;
                    }
                    var (codes, depth) = box.Palette!.Codes(column, format);
                    sources[k] = (planes[c], Columns(c), c, [.. codes.Select(v => (double)v)], 0, null);
                    bits[k] = depth;
                }
                else if (number && numbers.ElementAtOrDefault(c) is { } kept)
                {
                    sources[k] = (planes[c], Columns(c), c, null, 0, kept);
                    bits[k] = 16;
                    continue;
                }
                else
                {
                    // mantissa and exponent put together are a level of 16 bits, so is a signed fixed or floating point number; any other
                    // component has its own precision
                    var shift = raw.ElementAtOrDefault(c) is null && !(forms[k].Signed && forms[k].Number) ? Math.Max(0, cs.Output[c].Precision - 16) : 0;
                    sources[k] = (planes[c], Columns(c), c, null, shift, null);
                    bits[k] = precision[c] + shift;
                }
                fromZero[k] = forms[k].Signed && parameterized.MapsSigned;
            }
            var outside = 0;
            if (parameterized.MayLeaveGamut(n))
                Rows(height, count, y =>
                {
                    if (Volatile.Read(ref outside) != 0) return;
                    Span<double> codes = stackalloc double[n];
                    Span<int> rows = stackalloc int[n];
                    RowsOf(y, rows);
                    for (var x = 0; x < width; x++)
                    {
                        CodesAt(x, rows, codes);
                        if (!parameterized.Outside(codes, bits)) continue;
                        Volatile.Write(ref outside, 1);
                        return;
                    }
                });
            var wide = outside != 0;
            var precise = wide ? new byte[(long)count * n * 2] : null;
            Rows(height, count, y =>
            {
                Span<double> codes = stackalloc double[n];
                Span<byte> pixel = stackalloc byte[n];
                Span<ushort> exact = stackalloc ushort[n];
                Span<int> rows = stackalloc int[n];
                RowsOf(y, rows);
                var at = (long)y * width * n;
                for (var x = 0; x < width; x++, at += n)
                {
                    CodesAt(x, rows, codes);
                    if (precise is null)
                        parameterized.Pixel(codes, bits, pixel, wide);
                    else
                    {
                        parameterized.WidePixel(codes, bits, pixel, exact);
                        for (var k = 0; k < n; k++) (precise[(at + k) * 2], precise[(at + k) * 2 + 1]) = ((byte)(exact[k] >> 8), (byte)exact[k]);
                    }
                    for (var k = 0; k < n; k++) samples[at + k] = pixel[k];
                }
            });
            fine = precise;
            return wide;

            // the start of image row y in the plane of each channel
            void RowsOf(int y, Span<int> rows)
            {
                for (var k = 0; k < n; k++) rows[k] = Row(sources[k].Component, y) * planeW[sources[k].Component];
            }

            // the codes of the channels at column x; a signed channel as the code its value stands for
            void CodesAt(int x, ReadOnlySpan<int> rows, Span<double> codes)
            {
                for (var k = 0; k < n; k++)
                {
                    var source = sources[k];
                    if (source.Plane.Length == 0)
                    {
                        codes[k] = 0;
                        continue;
                    }
                    var at = rows[k] + source.Cols[x];
                    if (source.Numbers is { } kept)
                    {
                        codes[k] = JpxParameterized.NumberCode(kept[at]);
                        continue;
                    }
                    var value = source.Plane[at];
                    codes[k] = source.Palette is { } palette ? palette[Math.Min(value, palette.Length - 1)] : (long)value << source.Shift;
                    if (fromZero[k]) codes[k] = parameterized.FromZero(k, n, codes[k], bits[k]);
                }
            }
        }

        // map: raw value with its bits (and the bits a component above 16 was shifted down by) → 8 bits; null: stretched
        void Fill(byte[] target, int offset, int stride, (int Component, int PaletteColumn) channel, Func<int, int, int, byte>? map)
        {
            var c = channel.Component;
            var plane = planes[c];
            var cols = Columns(c);
            var bits = precision[c];
            if (plane.Length == 0) return;
            var down = Math.Max(0, cs.Output[c].Precision - 16);
            // /Indexed from the PDF page: indices unchanged (up to 255); a palette column; otherwise the component stretched to 8 bits
            var lookup = !applyPalette ? RawTable(bits, indexDecode)
                : channel.PaletteColumn >= 0 ? box.Palette!.Table(channel.PaletteColumn, map, paletteFormats.GetValueOrDefault(channel.PaletteColumn))
                : Table(bits, map is null ? null : v => map(v, bits, down));
            var last = lookup.Length - 1;
            // Without subsampling the component row lies exactly under the image row
            var direct = planeW[c] == width && cols.Length > 0 && cols[0] == 0 && cols[^1] == width - 1;
            Rows(height, count, y =>
            {
                var row = Row(c, y) * planeW[c];
                var at = (long)y * width * stride + offset;
                if (direct)
                {
                    for (var x = 0; x < width; x++, at += stride) target[at] = lookup[Math.Min(plane[row + x], last)];
                    return;
                }
                for (var x = 0; x < width; x++, at += stride) target[at] = lookup[Math.Min(plane[row + cols[x]], last)];
            });
        }
    }

    /// <summary>Table raw value → 8 bits: stretched, or through <paramref name="map"/>.</summary>
    internal static byte[] Table(int bits, Func<int, byte>? map = null)
    {
        var table = new byte[1 << bits];
        for (var v = 0; v < table.Length; v++) table[v] = map?.Invoke(v) ?? Stretch(v, bits);
        return table;
    }

    /// <summary>A value of <paramref name="bits"/> bits stretched to 8 bits (from 8 bits on the upper eight).</summary>
    internal static byte Stretch(int value, int bits) =>
        bits >= 8 ? (byte)(value >> (bits - 8)) : (byte)(value * 255 / Math.Max(1, (1 << bits) - 1));

    // ---- Colour specifications (ISO/IEC 15444-1, I.5.3.3; ISO/IEC 15444-2, M.11.7.2–M.11.7.4)

    /// <summary>
    /// The colour of <paramref name="n"/> colour channels: the first of <paramref name="specs"/> (best first) that is supported and fits them —
    /// the JPX baseline set of ISO/IEC 15444-2, M.9.2.4, that ISO 32000-2, 7.4.9 requires, except CIEJab, which PDF excludes; also CMYK (which
    /// PDF allows) and the two bi-level spaces; and the Parameterized specification with the ITU-T H.273 codes it can read (M.11.7.3.4). Map:
    /// per colour channel the raw value → 8 bits where the space needs more than stretching; Parameterized: the channels of a pixel together.
    /// <paramref name="forms"/>: how each colour channel is a number (signed, fixed or floating point, depth), for the mapping of M.11.7.2.
    /// </summary>
    internal static (JpxColor Color, byte[]? Icc, double[]? White, Func<int, int, int, int, byte>? Map, JpxParameterized? Parameterized) Choose(
        IReadOnlyList<JpxColourSpec> specs, int n, JpxChannelForm[]? forms = null)
    {
        // signed channels of sRGB and sRGB-grey (T.801 M.11.7.2 sentence 16; T.800 I.5.3.3 sentences 13 and 14): from the value of no light,
        // Dzero = Lzero = 0 — d = Dmax·x / MAX, MAX = 2^(n−1) − 1 (Table M.24bis; a signed fixed or floating point channel came as 16 bits of
        // the same coding, MAX standing for 1.0), the negative values below black. Before they were shown shifted by half their range, 0 as
        // mid-grey (docs/FEHLERGEDAECHTNIS.md, No. 1066)
        var fromZero = forms is not null && forms.Any(f => f.Signed) ? (Func<int, int, int, int, byte>)((k, v, b, _) =>
            k < forms.Length && forms[k].Signed ? FromZero(v, b) : Stretch(v, b)) : null;
        foreach (var spec in specs)
        {
            switch (spec.Method)
            {
                case 1:
                    switch (spec.EnumCs, n)
                    {
                        case (16, 3): return (JpxColor.Rgb, null, null, fromZero, null);               // sRGB
                        case (17, 1): return (JpxColor.Gray, null, null, fromZero, null);              // sRGB-grey
                        // sYCC, e-sYCC (PIMA 7667, Annex B; No. 944), e-sRGB, YCbCr(2), YCbCr(1), YCbCr(3), YPbPr(1125/60), YPbPr(1250/50): read
                        // in double precision by Assemble, as the Parameterized specification (docs/FEHLERGEDAECHTNIS.md, No. 1057, No. 1071)
                        case (18 or 24 or 20 or 3 or 1 or 4 or 22 or 23, 3):
                            var enumerated = JpxParameterized.Enumerated(spec.EnumCs)!;
                            var (enumeratedSpace, enumeratedProfile) = enumerated.Space(n);
                            return (enumeratedSpace, enumeratedProfile, null, null, enumerated);
                        case (21, 3): return (JpxColor.Icc, CalibratedProfile.RommRgb(), null, null, null);  // ROMM-RGB
                        case (14, 3):                                                                  // CIELab
                            var (white, lab) = Lab(spec.Parameters, forms);
                            return (JpxColor.Lab, null, white, lab, null);
                        case (12, 4): return (JpxColor.Cmyk, null, null, null, null);                  // CMYK
                        // Table M.25 (decided on 23.11.2026; before passed over): YCbCr(2) full range with the matrix of BT.601 — the same
                        // conversion as sYCC; YCbCr(1) and YCbCr(3) in the limited range of BT.601 (Y 16–235, Cb and Cr 16–240 at 8 bits,
                        // scaled with the bits), the same matrix (both above); CMY inverted (0 no ink, 2^BPS − 1 full ink); YCCK as YCbCr(2)
                        // of the inverted C, M, Y, K unchanged — in double precision like sYCC, without the 8-bit fixed point step it had
                        // before (No. 1072). Their signed channels stay shifted by half the range: the colour of no light has no unique values
                        // in them (CMY: Dmax itself; CMYK and YCCK: full ink of every kind or black alone), so M.11.7.2 gives no Dzero
                        case (11, 3): return (JpxColor.Rgb, null, null, (_, v, b, _) => (byte)(255 - Stretch(v, b)), null);   // CMY
                        case (13, 4): return (JpxColor.Cmyk, null, null, null, JpxParameterized.Enumerated(13));               // YCCK
                        // scRGB (25), linear, after the ICC Colour Encoding Registry (decided on 23.11.2026; before passed over, No. 1071):
                        // 16-bit codes and fixed or floating point channels, read in double precision by Assemble — other channels are
                        // passed over, the next specification counts (JpxParameterized.Enumerated)
                        case (25, 3) when ScRgbFits(forms):
                            var scrgb = JpxParameterized.Enumerated(25)!;
                            var (scrgbSpace, scrgbProfile) = scrgb.Space(n);
                            return (scrgbSpace, scrgbProfile, null, null, scrgb);
                        case (15, 1): return (JpxColor.Gray, null, null, null, null);                  // bi-level(2): 1 is white
                        case (0, 1): return (JpxColor.Gray, null, null, (_, v, b, _) => (byte)(255 - Stretch(v, b)), null);   // bi-level: 1 is black
                        // not read, the next specification counts: PhotoYCC (9) — its encoding is given only in Kodak's Photo CD documents
                        // (Table M.25: Part No. DC1200R, PCD045), which are not at hand; scRGB grey (26) — Table M.25 refers to IEC 61966-2-2
                        // for it, which is not at hand, and the ICC Colour Encoding Registry, which fixes scRGB itself, does not register a
                        // grey: neither its codes nor whether it is linear or non-linear are known — so it is not guessed
                    }
                    break;
                case 2 or 3 when spec.Profile is { } icc && IccProfile.Parse(icc) is { } profile && profile.Channels == n:
                    return (JpxColor.Icc, icc, null, null, null);
                // ITU-T H.273 codes (decided on 23.11.2026; before passed over): read in double precision by Assemble, written with the
                // sRGB tone curve; codes that are not applicable give way to the next specification
                case 5 when JpxParameterized.Fits(n) && JpxParameterized.Read(spec.Parameters) is { } parameterized:
                    var (space, profileOfPrimaries) = parameterized.Space(n);
                    return (space, profileOfPrimaries, null, null, parameterized);
            }
        }
        return (JpxColor.Unknown, null, null, null, null);
    }

    /// <summary>
    /// Whether the colour channels can be read as scRGB (<see cref="JpxParameterized.Enumerated"/>, value 25): each an unsigned integer of 16
    /// bits or a fixed or floating point number — the encodings the ICC Colour Encoding Registry fixes. Without the forms (the count of
    /// channels alone, <see cref="OpacityLast"/>) the three channels fit.
    /// </summary>
    private static bool ScRgbFits(JpxChannelForm[]? forms) =>
        forms is null || forms.Length == 3 && forms.All(f => f.Number || !f.Signed && f.Depth == 16);

    /// <summary>
    /// Whether fixed and floating point channels are kept as the numbers they are besides their levels of 0 … 1 (<see cref="JpxPlanes.Numbers"/>):
    /// for an image with an scRGB colour specification, whose numbers are linear light up to 7.5 and down to −0.5. Every other image is stored
    /// as before.
    /// </summary>
    internal static bool KeepsNumbers(JpxBoxes box) => box.Colours.Any(s => s is { Method: 1, EnumCs: 25 });

    /// <summary>⌈log2 m⌉ for m ≥ 1 (Equation M-18); 0 for m below 2.</summary>
    private static int CeilLog2(long m) => m <= 1 ? 0 : 64 - (int)long.LeadingZeroCount(m - 1);

    /// <summary>
    /// A signed channel from its value of no light 0 (T.801 M.11.7.2: d = Dzero + (Dmax − Dzero)·x / MAX with Dzero 0): the sample of
    /// <paramref name="bits"/> bits shifted by half its range (Store) → x, 8 bits = 255·x / (2^(bits−1) − 1), the negative values 0.
    /// </summary>
    internal static byte FromZero(int value, int bits)
    {
        var half = Math.ScaleB(1.0, bits - 1);
        return (byte)Math.Round(Math.Clamp(value - half, 0, Math.Max(1, half - 1)) * 255 / Math.Max(1, half - 1));
    }

    /// <summary>
    /// CIELab (ISO/IEC 15444-2, M.11.7.4.1, Equation M-18 solved for L*, a*, b*): L* = RL·(N − 2^⌈log2 MAX⌉·OL / 2^n) / MAX, a* and b* the same
    /// with their range and offset, n the bit depth of the channel; MAX after Table M.24bis — 2^n − 1 for unsigned and 2^(n−1) − 1 for signed
    /// integers, 1.0 for fixed and floating point numbers. So an offset counts as itself for unsigned integers, as half of it for signed ones
    /// and as O / 2^n of 1.0 for numbers. Without parameters the ranges 100, 170, 200 and the offsets of Table M.29bis: 0, 2^(n−1),
    /// 2^(n−2) + 2^(n−3) for unsigned channels, 0 for signed ones; under D50. Written in the encoding of <see cref="JpxColor.Lab"/>.
    /// Before signed channels were read like unsigned ones (docs/FEHLERGEDAECHTNIS.md, No. 974); before 23.11.2026 an offset given in EP counted
    /// in full on signed integers, and as an integer on numbers (No. 1068), and signed numbers lost their negative half (No. 1067).
    /// </summary>
    private static (double[] White, Func<int, int, int, int, byte> Map) Lab(byte[]? ep, JpxChannelForm[]? forms = null)
    {
        uint[]? fields = ep is { Length: >= 24 } ? [.. Enumerable.Range(0, 6).Select(i => U32(ep, i * 4))] : null;
        var white = Illuminant(ep is { Length: >= 28 } ? U32(ep, 24) : 0x00443530);
        return (white, (k, value, bits, down) =>
        {
            var form = forms is not null && k < forms.Length ? forms[k] : new JpxChannelForm(false, false, bits + down);
            k = Math.Min(k, 2);
            double range = fields?[2 * k] ?? k switch { 0 => 100, 1 => 170, _ => 200 };
            // the stored sample and MAX in its units (Table M.24bis): a signed sample came shifted by half its range (Store, JpxPalette) — N is
            // the value less that half; a number came as a level whose top (unsigned) or top above the middle (signed) is 1.0
            var half = Math.ScaleB(1.0, bits - 1);
            var sample = form.Signed ? value - half : value;
            var max = Math.Max(1, form.Signed ? half - 1 : 2 * half - 1);
            // the offset O (EP or Table M.29bis), by Equation M-18 2^⌈log2 MAX⌉·O / 2^n — with MAX of n bits for an integer, 1.0 for a number
            // —, then in the units of the sample: an integer of n bits was stored with its top bits (n − bits fewer)
            var n = Math.Clamp(form.Depth, 1, MaxDepth);
            var given = fields is not null ? fields[2 * k + 1]
                : form.Signed ? 0
                : k switch { 0 => 0, 1 => Math.ScaleB(1.0, n - 1), _ => Math.ScaleB(1.0, n - 2) + Math.ScaleB(1.0, n - 3) };
            var offset = form.Number ? given / Math.ScaleB(1.0, n) * max
                : given * Math.ScaleB(1.0, CeilLog2(form.Signed ? (1L << (n - 1)) - 1 : (1L << n) - 1) - n) / Math.ScaleB(1.0, Math.Max(0, n - bits));
            var v = (sample - offset) * range / max;
            return (byte)Math.Clamp(Math.Round(k == 0 ? v * 255 / 100 : v + 128), 0, 255);
        });
    }

    /// <summary>
    /// The white point (CIE XYZ, 2° observer, Y = 1) of the illuminant field IL (ISO/IEC 15444-2, Table M.29; ITU-T T.4, Annex E): a standard
    /// illuminant, or „CT“ with a colour temperature (CIE daylight locus, 4 000–25 000 K); anything else D50.
    /// </summary>
    internal static double[] Illuminant(uint il)
    {
        switch (il)
        {
            case 0x00443530: return [0.96422, 1, 0.82521];   // D50
            case 0x00443635: return [0.95047, 1, 1.08883];   // D65
            case 0x00443735: return [0.94972, 1, 1.22638];   // D75
            case 0x00005341: return [1.09850, 1, 0.35585];   // A
            case 0x00005343: return [0.98074, 1, 1.18232];   // C
            case 0x00004632: return [0.99186, 1, 0.67393];   // F2
            case 0x00004637: return [0.95041, 1, 1.08747];   // F7
            case 0x00463131: return [1.00962, 1, 0.64350];   // F11
        }
        if (il >> 16 == 0x4354 && (il & 0xFFFF) is var t and >= 4000 and <= 25000)
        {
            double k = t, x = t <= 7000
                ? -4.6070e9 / (k * k * k) + 2.9678e6 / (k * k) + 0.09911e3 / k + 0.244063
                : -2.0064e9 / (k * k * k) + 1.9018e6 / (k * k) + 0.24748e3 / k + 0.237040;
            var y = -3.000 * x * x + 2.870 * x - 0.275;
            return [x / y, 1, (1 - x - y) / y];
        }
        return [0.96422, 1, 0.82521];
    }

    private static uint U32(byte[] d, int i) => (uint)(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3]);

    /// <summary>
    /// The raw indices for an /Indexed colour space of the page (up to 255) — with its /Decode mapped as ISO 32000-2, 8.9.5.2 asks, with the
    /// bits of the JPEG 2000 component (Table 90: [0 2^n − 1] by default): a /ColorSpace is present, so /Decode counts for JPEG 2000 too
    /// (7.4.9). Before /Decode was passed over for /Indexed (S-A A78; docs/FEHLERGEDAECHTNIS.md, No. 945). Display and blacking out read
    /// the same mapped indices.
    /// </summary>
    private static byte[] RawTable(int bits, double[]? decode = null)
    {
        var table = new byte[1 << bits];
        var max = (1 << bits) - 1;
        for (var v = 0; v < table.Length; v++)
            table[v] = decode is [var low, var high, ..] && max > 0
                ? (byte)Math.Clamp(Math.Round(low + v * (high - low) / max), 0, 255)
                : (byte)Math.Min(v, 255);
        return table;
    }

    /// <summary>Runs over the rows [0, rows); from 64 K values on, distributed over the cores.</summary>
    private static void Rows(int rows, long values, Action<int> body)
    {
        if (values < 1 << 16)
        {
            for (var y = 0; y < rows; y++) body(y);
            return;
        }
        Parallel.For(0, rows, body);
    }
}

/// <summary>Decoded components at output size (raw values up to 16 bits), before palette and channel mapping.</summary>
internal sealed record JpxPlanes(JpxCodestream Codestream, JpxBoxes Boxes, ushort[][] Planes, int[] PlaneWidth, int[] PlaneHeight, long[] PlaneX0,
    long[] PlaneY0, int Width, int Height, int Reduce, bool Truncated)
{
    /// <summary>Memory budget of decoding; assembly continues to count against it.</summary>
    public JpxDecodeState Budget { get; init; } = null!;

    /// <summary>The pixel format of each component (ISO/IEC 15444-2, M.11.7.8); 0 for integers.</summary>
    public int[] Formats { get; init; } = [];

    /// <summary>The pixel format of palette columns, by column.</summary>
    public Dictionary<int, int> PaletteFormats { get; init; } = [];

    /// <summary>The integers of mantissa and exponent components (null for the others), for <c>Assemble</c> to put each pair together.</summary>
    public int[]?[] Raw { get; init; } = [];

    /// <summary>
    /// The numbers of fixed and floating point components as they are, beyond 0 … 1 too (null for the others, and for every image without an
    /// scRGB colour specification; see <see cref="JpxDecoder.KeepsNumbers"/>).
    /// </summary>
    public float[]?[] Numbers { get; init; } = [];
}

/// <summary>
/// How the values of a colour channel are numbers (ISO/IEC 15444-2, M.11.7.2, Table M.24bis): signed or not (Ssiz of a component, Bi of a
/// palette column), a fixed or floating point number (Pixel Format box; MAX 1.0) or an integer (MAX 2^n − 1, signed 2^(n−1) − 1), and the bit
/// depth n of the channel.
/// </summary>
internal readonly record struct JpxChannelForm(bool Signed, bool Number, int Depth);

/// <summary>One Colour Specification box (ISO/IEC 15444-1, I.5.3.3; ISO/IEC 15444-2, M.11.7.2).</summary>
/// <param name="Method">METH: 1 enumerated, 2 restricted ICC, 3 any ICC, 4 vendor, 5 parameterized.</param>
/// <param name="Precedence">PREC (signed).</param>
/// <param name="Approx">APPROX: 1 accurate … 4 poor; 0 in JP2.</param>
/// <param name="EnumCs">EnumCS of the enumerated method.</param>
/// <param name="Parameters">EP of the enumerated method (CIELab); METHDAT of the parameterized method; null without.</param>
/// <param name="Profile">The ICC profile of the ICC methods.</param>
/// <param name="Order">Position in the file.</param>
internal sealed record JpxColourSpec(int Method, int Precedence, int Approx, uint EnumCs, byte[]? Parameters, byte[]? Profile, int Order);

/// <summary>
/// JP2/JPX wrapper (ISO/IEC 15444-1, Annex I; ISO/IEC 15444-2, Annex M, as far as JPX baseline needs it, M.9.2): where the first codestream lies
/// (a Contiguous Codestream box or a Fragment Table box), and for the first compositing layer the colour specifications, palette, component
/// mapping, channel definition and opacity.
/// </summary>
internal sealed class JpxBoxes
{
    private const uint Jp2c = 0x6A703263, Jp2h = 0x6A703268, Jpch = 0x6A706368, Jplh = 0x6A706C68, Grp = 0x67727020, Cgrp = 0x63677270, Colr = 0x636F6C72, Pclr = 0x70636C72,
        Cmap = 0x636D6170, Cdef = 0x63646566, Opct = 0x6F706374, Ftbl = 0x6674626C, Flst = 0x666C7374, Cref = 0x63726566, Pxfm = 0x7078666D, Rreq = 0x72726571;

    /// <summary>The array the codestream lies in: the data itself, or its fragments put together.</summary>
    public byte[] Data = [];
    public int Start;
    public int End;

    /// <summary>The colour specifications of the first compositing layer, best first: precedence, then approximation, then file order.</summary>
    public List<JpxColourSpec> Colours = [];
    public JpxPalette? Palette;
    public List<(int Component, int Type, int Column)>? Mapping;
    public List<(int Channel, int Type, int Association)>? Definitions;

    /// <summary>Otyp of the Opacity box of the first compositing layer (ISO/IEC 15444-2, M.11.7.6); null without.</summary>
    public int? OpacityType;

    /// <summary>The Pixel Format box of the JP2 Header box or the first compositing layer (ISO/IEC 15444-2, M.11.7.8): channel → format, only
    /// the formats other than integers; null without.</summary>
    public Dictionary<int, int>? PixelFormats;

    /// <summary>What the Reader Requirements box asks for to display the image and the decoder lacks (M.6.3); empty without one.</summary>
    public int[] MissingFeatures = [];

    /// <summary>The contents of the Reader Requirements box, for its evaluation once the colour is chosen (scRGB, <see cref="JpxRequirements.ScRgb"/>);
    /// null without one.</summary>
    public byte[]? Requirements;

    /// <summary>
    /// A box had a reserved or invalid value the decoder reads past (docs/FEHLERGEDAECHTNIS.md, No. 1050): a reserved box length (LBox 2 to 7,
    /// I.4), a palette column deeper than 38 bits (Table I.13), a component mapping type MTYP of 2 to 255 (Table I.14) or a component the
    /// codestream does not have (CMPi), a colour channel without an associated colour (Tables I.16, I.17: type 0 with association 2^16 − 1), a
    /// colour specification method other than 1 and 2 in a JP2 file (Table I.9), a reserved one (0, 6 to 255) in a JPX file (T.801 Table M.22;
    /// see <see cref="ColourSpec"/>). The image is shown as before, and counts as damaged.
    /// </summary>
    public bool Damaged;

    /// <summary>
    /// Whether the file is a JP2 file and not a JPX file: brand „jp2 “ in the File Type box, and „jpx “ not among its compatible brands
    /// (T.800 I.5.2; a JPX file lists „jpx “, T.801 M.2.1). Only there are the colour specification methods of T.801 reserved.
    /// </summary>
    private bool _plainJp2;

    private const uint Jp2Brand = 0x6A703220, JpxBrand = 0x6A707820;

    public static JpxBoxes Read(byte[] data)
    {
        var boxes = new JpxBoxes { Data = data, Start = 0, End = data.Length };
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0x4F) return boxes;
        // neither a codestream (SOC) nor a JP2/JPX file whose boxes name one: no image, as ISO 32000-2, 7.4.9 and the three reference readers
        // have it — before, the first 4 KB were searched for the start of a codestream behind other bytes or a damaged wrapper, and PDF-Knead
        // showed an image no other reader showed; no file of the collections needs it (S-A A11; docs/FEHLERGEDAECHTNIS.md, No. 939)
        // a JP2/JPX file begins with the signature box and the file type box (ISO/IEC 15444-1, I.5.1, I.5.2); without them the three
        // reference readers show no image, and PDF-Knead read the boxes anyway (S-A A112; docs/FEHLERGEDAECHTNIS.md, No. 970)
        if (!Signed(data)) throw new JpxException("JPX: keine JP2-Datei (Signatur- oder Dateityp-Box fehlt)");
        boxes._plainJp2 = PlainJp2(data);
        if (!boxes.ReadFile(data)) throw new JpxException("JPX: kein Datenstrom");
        return boxes;
    }

    /// <summary>The fixed signature box of 12 bytes first, the file type box right after it (I.5.1, I.5.2).</summary>
    private static bool Signed(byte[] d) =>
        d.Length >= 20 && U32(d, 0) == 12 && U32(d, 4) == 0x6A502020 && U32(d, 8) == 0x0D0A870A && U32(d, 16) == 0x66747970;

    /// <summary>
    /// Brand „jp2 “ and no „jpx “ among the compatible brands of the File Type box right behind the signature (<see cref="Signed"/>): BR at
    /// byte 20, CL from byte 28 to the end of the box (I.5.2, Table I.3). A box length below its 16 fixed bytes (also 0 and 1) gives no list.
    /// </summary>
    private static bool PlainJp2(byte[] d)
    {
        if (d.Length < 24 || U32(d, 20) != Jp2Brand) return false;
        long length = U32(d, 12);
        var end = length < 16 ? 28 : (int)Math.Min(d.Length, 12 + length);
        for (var i = 28; i + 4 <= end; i += 4)
            if (U32(d, i) == JpxBrand) return false;
        return true;
    }

    private static uint U32(byte[] d, int i) => (uint)(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3]);

    /// <summary>
    /// The boxes in [pos, end): type and contents; a length beyond the end is cut to it (damaged files). A reserved length (LBox 2 to 7, I.4)
    /// reaches to the end, as before, and marks <paramref name="owner"/> damaged.
    /// </summary>
    private static IEnumerable<(uint Type, int Start, int End)> Children(byte[] d, int pos, int end, JpxBoxes? owner = null)
    {
        while (pos + 8 <= end)
        {
            long length = U32(d, pos);
            var type = U32(d, pos + 4);
            var header = 8;
            if (length == 1)
            {
                if (pos + 16 > end) yield break;
                length = (long)U32(d, pos + 8) << 32 | U32(d, pos + 12);
                header = 16;
            }
            else if (length == 0)
            {
                length = end - pos;
            }
            else if (length < 8 && owner is not null)
            {
                owner.Damaged = true;
            }
            // compared without adding: an XLBox near 2^63 made pos + length run over, the position jumped back and the loop never ended (M2, T.800 I.4, 23.11.2026)
            if (length < header || length > end - pos) length = end - pos;
            yield return (type, pos + header, (int)(pos + length));
            pos = (int)(pos + length);
        }
    }

    /// <summary>
    /// The boxes of the top level, the contents of Grouping boxes in their place: what a Grouping box holds counts as if it stood at its level
    /// (M.11.25; docs/FEHLERGEDAECHTNIS.md, No. 968); nested at most eight deep.
    /// </summary>
    private IEnumerable<(uint Type, int Start, int End)> TopLevel(byte[] d, int s, int e, int depth)
    {
        foreach (var box in Children(d, s, e, this))
        {
            if (box.Type == Grp && depth < 8)
            {
                foreach (var inner in TopLevel(d, box.Start, box.End, depth + 1)) yield return inner;
            }
            else
            {
                yield return box;
            }
        }
    }

    /// <summary>Reads the top level; false if there is no codestream in it.</summary>
    private bool ReadFile(byte[] d)
    {
        var codestream = false;
        var firstCodestreamSeen = false;
        var header = new List<JpxColourSpec>();
        List<JpxColourSpec>? layer = null;
        List<(int, int, int)>? layerDefinitions = null;
        var firstLayer = true;
        var firstCodestreamHeader = true;
        var order = 0;
        foreach (var (type, s, e) in TopLevel(d, 0, d.Length, 0))
        {
            switch (type)
            {
                // the first codestream: in a Contiguous Codestream box, or in fragments listed by a Fragment Table box, which counts the same
                // (M.11.3); the JPX baseline file has its fragments in the file itself (M.9.2.5)
                // only the first one: when its fragments cannot be put together (a fragment in another file), no later codestream takes its
                // place — the image is the first codestream's (M.9.2.2 sentence 5; before a later Contiguous Codestream box was shown,
                // docs/FEHLERGEDAECHTNIS.md, No. 985)
                case Jp2c when !firstCodestreamSeen:
                    firstCodestreamSeen = true;
                    (Start, End, codestream) = (s, e, true);
                    break;
                case Ftbl when !firstCodestreamSeen:
                    firstCodestreamSeen = true;
                    if (Fragments(d, s, e) is { } joined) (Data, Start, End, codestream) = (joined, 0, joined.Length, true);
                    break;
                case Jp2h:
                    foreach (var (inner, si, ei) in Children(d, s, e, this)) ReadHeaderBox(d, inner, si, ei, header, ref order);
                    break;
                // the Reader Requirements box (M.11.1): tested against what the decoder can do and reported, the image still shown (decided
                // on 23.11.2026; before it was passed over)
                case Rreq:
                    MissingFeatures = JpxRequirements.Missing(d, s, e);
                    Requirements = d[s..e];
                    break;
                // the Codestream Header box of the first codestream: its boxes add to those of the JP2 Header box, or take their place
                // (M.5.2.1); the JPX baseline only forbids the same box in both (M.9.2) — before, it was passed over and a palette in it was
                // lost (docs/FEHLERGEDAECHTNIS.md, No. 968)
                case Jpch when firstCodestreamHeader:
                    firstCodestreamHeader = false;
                    foreach (var (child, cs, ce) in Children(d, s, e, this))
                        if (Resolve(d, d, child, cs, ce) is { } box) ReadHeaderBox(box.Data, box.Type, box.Start, box.End, header, ref order);
                    break;
                // the first Compositing Layer Header box: its colour group replaces the colour specifications of the JP2 Header box, its channel
                // definition the default, and an Opacity box takes the place of both channel definitions (M.11.7, M.9.2.2)
                case Jplh when firstLayer:
                    firstLayer = false;
                    foreach (var (child, cs, ce) in Children(d, s, e, this))
                    {
                        if (Resolve(d, d, child, cs, ce) is not { } box) continue;
                        var (inner, b, si, ei) = box;
                        switch (inner)
                        {
                            case Cgrp:
                                // its Colour Specification boxes, each also given by a Cross-Reference box (M.5.2.2, M.11.4 — the conformance file
                                // reboxing.jpx of T.803 has one; before it was passed over, docs/FEHLERGEDAECHTNIS.md, No. 975)
                                layer ??= [];
                                foreach (var (member, sm, em) in Children(b, si, ei, this))
                                    if (Resolve(d, b, member, sm, em) is { Type: Colr } colr && ColourSpec(colr.Data, colr.Start, colr.End, order++) is { } spec)
                                        layer.Add(spec);
                                break;
                            case Opct when ei > si:
                                OpacityType = b[si];
                                break;
                            case Cdef:
                                layerDefinitions = Channels(b, si, ei);
                                ColourWithoutAssociation(layerDefinitions);
                                break;
                            case Pxfm:
                                PixelFormats = PixelFormatOf(b, si, ei);
                                break;
                        }
                    }
                    break;
                default:
                    // misplaced at the top level by some producers: read like inside the JP2 Header box, as the three reference readers
                    // do (S-A A111)
                    ReadHeaderBox(d, type, s, e, header, ref order);
                    break;
            }
        }
        var colours = layer is { Count: > 0 } ? layer : header;
        Colours = [.. colours.OrderByDescending(c => c.Precedence).ThenBy(c => c.Approx).ThenBy(c => c.Order)];
        if (layerDefinitions is not null) Definitions = layerDefinitions;
        if (OpacityType is not null) Definitions = null;
        return codestream && End > Start;
    }

    /// <summary>
    /// A box inside a header superbox as it counts: a Cross-Reference box stands for the box its fragments make up — Rtyp its type, the fragments
    /// its contents (of a superbox: its boxes) — within the file only (M.9.2.6), never another Cross-Reference box (M.11.4; No. 949); null if
    /// it cannot be resolved. The offsets of the fragments count from the start of the file (M.11.3), also for a Cross-Reference box inside a
    /// box that itself came from fragments (before they were read in the copied bytes of that box). All the boxes resolved in a file together
    /// are never larger than it: a crafted file with thousands of Cross-Reference boxes over the same large range made a copy of up to the
    /// file size for each (probe of T.801, 23.11.2026, B15).
    /// </summary>
    /// <param name="file">The whole file, where the fragments lie.</param>
    /// <param name="d">The data holding the box (the file, or the contents of a box resolved before).</param>
    private (uint Type, byte[] Data, int Start, int End)? Resolve(byte[] file, byte[] d, uint type, int s, int e)
    {
        if (type != Cref) return (type, d, s, e);
        if (e - s < 4 || U32(d, s) == Cref || Fragments(d, s + 4, e, file, file.Length - _resolvedBytes) is not { } referenced) return null;
        _resolvedBytes += referenced.Length;
        return (U32(d, s), referenced, 0, referenced.Length);
    }

    /// <summary>The bytes of all boxes resolved from Cross-Reference boxes so far (<see cref="Resolve"/>).</summary>
    private long _resolvedBytes;

    /// <summary>A box of the JP2 Header box: colour specification, palette, component mapping, channel definition.</summary>
    private void ReadHeaderBox(byte[] d, uint type, int s, int e, List<JpxColourSpec> colours, ref int order)
    {
        switch (type)
        {
            case Colr when ColourSpec(d, s, e, order++) is { } spec:
                colours.Add(spec);
                break;
            case Pclr:
                Palette = JpxPalette.Read(d, s, e);
                if (Palette is { Reserved: true }) Damaged = true;
                break;
            case Cmap:
                Mapping = [];
                for (var i = s; i + 4 <= e; i += 4) Mapping.Add((d[i] << 8 | d[i + 1], d[i + 2], d[i + 3]));
                // MTYP 2 to 255 is reserved (Table I.14): the channel is read from the component directly, as with 0, and reported
                if (Mapping.Any(m => m.Type > 1)) Damaged = true;
                break;
            case Cdef:
                Definitions = Channels(d, s, e);
                ColourWithoutAssociation(Definitions);
                break;
            case Pxfm:
                PixelFormats = PixelFormatOf(d, s, e);
                break;
        }
    }

    /// <summary>
    /// A Pixel Format box (ISO/IEC 15444-2, M.11.7.8): per channel how its bit patterns are read — integers, mantissas, exponents, fixed or
    /// floating point numbers (<see cref="JpxPixelFormat"/>); null if it names only integers, what the decoder does anyway. A reserved format
    /// is refused: its numbers would be shown with meaningless values (docs/FEHLERGEDAECHTNIS.md, No. 969).
    /// </summary>
    private static Dictionary<int, int>? PixelFormatOf(byte[] d, int s, int e)
    {
        if (e - s < 2) return null;
        var n = d[s] << 8 | d[s + 1];
        Dictionary<int, int>? formats = null;
        for (var i = 0; i < n && s + 6 + 4 * i <= e; i++)
        {
            int channel = d[s + 2 + 4 * i] << 8 | d[s + 3 + 4 * i], format = d[s + 4 + 4 * i] << 8 | d[s + 5 + 4 * i];
            if (!JpxPixelFormat.Known(format)) throw new JpxException("JPX: Pixelformat unbekannt");
            if (format != 0) (formats ??= [])[channel] = format;
        }
        return formats;
    }

    /// <summary>
    /// A Colour Specification box; null if it is too short to say anything. In a JP2 file (<see cref="_plainJp2"/>) T.800 Table I.9 reserves
    /// every method but 1 and 2: such a box — the ICC profile of any kind (3), a vendor method (4), the parameterized one (5) of T.801 —
    /// is read as in a JPX file, as before, and the image counts as damaged (decided on 23.11.2026: read and report; before without a word,
    /// docs/FEHLERGEDAECHTNIS.md, No. 1061). In a JPX file the methods 0 and 6 to 255 are reserved (T.801 Table M.22): such a box is passed
    /// over as the table asks — never chosen, the image unchanged — and the image counts as damaged (decided on 23.11.2026: read and report;
    /// before without a word, No. 1073).
    /// </summary>
    private JpxColourSpec? ColourSpec(byte[] d, int s, int e, int order)
    {
        if (e - s < 3) return null;
        int method = d[s], precedence = (sbyte)d[s + 1], approx = d[s + 2];
        if (_plainJp2 ? method is not (1 or 2) : method is not (>= 1 and <= 5)) Damaged = true;
        return method switch
        {
            1 when e - s >= 7 => new JpxColourSpec(1, precedence, approx, U32(d, s + 3), e - s > 7 ? d[(s + 7)..e] : null, null, order),
            2 or 3 when e - s > 3 => new JpxColourSpec(method, precedence, approx, 0, null, d[(s + 3)..e], order),
            // Parameterized (ISO/IEC 15444-2, M.11.7.3.4): METHDAT kept as the parameters, read by JpxParameterized (shorter than 7 bytes:
            // not understood, never chosen)
            5 => new JpxColourSpec(5, precedence, approx, 0, d[(s + 3)..e], null, order),
            _ => new JpxColourSpec(method, precedence, approx, 0, null, null, order),   // not understood: kept in order, never chosen
        };
    }

    /// <summary>
    /// A colour channel (type 0) whose association says it belongs to no colour (2^16 − 1, Table I.17: no colour;
    /// type 0 is colour image data, Table I.16): it stays a colour channel, as before, and the image counts as damaged.
    /// </summary>
    private void ColourWithoutAssociation(List<(int Channel, int Type, int Association)>? definitions)
    {
        if (definitions is not null && definitions.Any(d => d.Type == 0 && d.Association == 65535)) Damaged = true;
    }

    /// <summary>A Channel Definition box: channel, type, association.</summary>
    private static List<(int, int, int)>? Channels(byte[] d, int s, int e)
    {
        if (e - s < 2) return null;
        var list = new List<(int, int, int)>();
        var n = d[s] << 8 | d[s + 1];
        for (var i = 0; i < n && s + 2 + 6 * i + 6 <= e; i++)
        {
            var p = s + 2 + 6 * i;
            list.Add((d[p] << 8 | d[p + 1], d[p + 2] << 8 | d[p + 3], d[p + 4] << 8 | d[p + 5]));
        }
        return list;
    }

    /// <summary>
    /// The fragments of a Fragment List box put together in their order: the codestream of a Fragment Table box (ISO/IEC 15444-2, M.11.3), the
    /// contents of the box a Cross-Reference box stands for (M.11.4). Null if one lies in another file (data reference not 0: not followed,
    /// JPX baseline keeps them in the file, M.9.2.5, M.9.2.6), outside the file, or together larger than <paramref name="limit"/>.
    /// </summary>
    /// <param name="d">The data holding the Fragment List box.</param>
    /// <param name="s">Start of the contents of the box holding the list.</param>
    /// <param name="e">End of those contents.</param>
    /// <param name="file">The whole file, where the fragments lie; null: <paramref name="d"/> is the file.</param>
    /// <param name="limit">The most bytes the fragments may make up together (beyond the file size, which they never exceed).</param>
    private static byte[]? Fragments(byte[] d, int s, int e, byte[]? file = null, long limit = long.MaxValue)
    {
        file ??= d;
        foreach (var (type, fs, fe) in Children(d, s, e))
        {
            if (type != Flst || fe - fs < 2) continue;
            var n = d[fs] << 8 | d[fs + 1];
            if (n == 0 || fe - fs < 2 + 14L * n) return null;
            var parts = new (int Offset, int Length)[n];
            long total = 0;
            for (var i = 0; i < n; i++)
            {
                var p = fs + 2 + 14 * i;
                var offset = (ulong)U32(d, p) << 32 | U32(d, p + 4);
                var length = U32(d, p + 8);
                var reference = d[p + 12] << 8 | d[p + 13];
                // a fragment begins behind the signature box at the earliest (T.801, Table M.17: OFF at least 12) — before one could point into it
                // (M2, T.801 M.11.3.1, 23.11.2026)
                if (reference != 0 || offset < 12 || offset > (ulong)file.Length || offset + length > (ulong)file.Length) return null;
                parts[i] = ((int)offset, (int)length);
                total += length;
            }
            // the fragments are parts of this file: together never larger than it (crafted lists: the same large range many times over)
            if (total == 0 || total > file.Length || total > limit) return null;
            var joined = new byte[total];
            var at = 0;
            foreach (var (offset, length) in parts)
            {
                Array.Copy(file, offset, joined, at, length);
                at += length;
            }
            return joined;
        }
        return null;
    }
}

/// <summary>
/// Palette (pclr): one value per column for each entry, any bit depth up to 38 (stored with at most 16 bits). The entries of a signed column
/// (Bi with its top bit) are kept shifted by half their range, as the samples of a signed component are (decided on 23.11.2026: palette
/// columns as components; before their two's complement bits were read as an unsigned value, −1 as the brightest, docs/FEHLERGEDAECHTNIS.md,
/// No. 1070).
/// </summary>
internal sealed class JpxPalette
{
    private readonly int[][] _values;
    private readonly int[] _bits;

    /// <summary>The entries with all their bits as integers (signed where Bi says so), for a column with a pixel format (M.11.7.8).</summary>
    private readonly long[][] _integers;

    /// <summary>The bit depth of each column as Bi gives it (up to 38).</summary>
    private readonly int[] _depth;

    /// <summary>Whether each column is signed (Bi, Table I.13).</summary>
    private readonly bool[] _signed;

    private JpxPalette(int[][] values, int[] bits, long[][] integers, int[] depth, bool[] signed)
    {
        _values = values;
        _bits = bits;
        _integers = integers;
        _depth = depth;
        _signed = signed;
    }

    public int Columns => _bits.Length;

    /// <summary>Whether a column is signed (Bi).</summary>
    public bool Signed(int column) => _signed[column];

    /// <summary>The bit depth of a column (Bi, up to 38).</summary>
    public int Depth(int column) => _depth[column];

    /// <summary>A column had a reserved depth of 39 to 128 bits (Table I.13): read with its top 38 bits.</summary>
    public bool Reserved { get; private init; }

    public static JpxPalette? Read(byte[] d, int s, int e)
    {
        if (e - s < 3) return null;
        var entries = Math.Min(1024, d[s] << 8 | d[s + 1]);
        var columns = d[s + 2];
        if (entries == 0 || columns == 0 || e - s < 3 + columns) return null;
        var bits = new int[columns];
        var signed = new bool[columns];
        // the bytes each entry of a column takes as stored: Bi + 1 bits rounded up, also for a reserved depth (Table I.13: 39 to 128 bits)
        var storedBytes = new int[columns];
        var reserved = false;
        for (var c = 0; c < columns; c++)
        {
            bits[c] = (d[s + 3 + c] & 0x7F) + 1;
            signed[c] = (d[s + 3 + c] & 0x80) != 0;
            storedBytes[c] = (bits[c] + 7) / 8;
            // a reserved depth: the column is read with its top 38 bits, a value as with a column of 38 bits — before the entry ran over the
            // 64-bit register and the shifts by more than 63 bits wrapped round (docs/FEHLERGEDAECHTNIS.md, No. 1050)
            if (bits[c] > 38)
            {
                bits[c] = 38;
                reserved = true;
            }
        }
        var depth = (int[])bits.Clone();
        var values = new int[columns][];
        var integers = new long[columns][];
        for (var c = 0; c < columns; c++)
        {
            values[c] = new int[entries];
            integers[c] = new long[entries];
        }
        var p = s + 3 + columns;
        for (var i = 0; i < entries; i++)
        for (var c = 0; c < columns; c++)
        {
            var bytes = storedBytes[c];
            UInt128 wide = 0;
            for (var b = 0; b < bytes; b++) wide = wide << 8 | (uint)(p < e ? d[p++] : 0);
            // the top 38 of a reserved depth (its bit depth as given, (Bi & 0x7F) + 1)
            var given = (d[s + 3 + c] & 0x7F) + 1;
            var v = (long)(given > bits[c] ? wide >> (given - bits[c]) : wide);
            values[c][i] = (int)(bits[c] > 16 ? v >> (bits[c] - 16) : v);
            integers[c][i] = signed[c] && (v >> (bits[c] - 1) & 1) != 0 ? v - (1L << bits[c]) : v;
        }
        // an entry above the depth Bi gives (ISO/IEC 15444-1, I.5.3.4: Bi bits) — a faulty file: the column is read with the width its entries
        // are stored in; in the file that showed it the /Indexed palette of the PDF holds the same bytes, so these are the colours meant (S-A
        // A113). Before such a value was stretched as if it had Bi bits and ran over into wrong colours
        for (var c = 0; c < columns; c++)
        {
            var stored = (bits[c] + 7) / 8 * 8;
            if (stored > bits[c] && bits[c] <= 16 && values[c].Any(v => v >> bits[c] != 0)) bits[c] = depth[c] = stored;
        }
        for (var c = 0; c < columns; c++) bits[c] = Math.Min(16, bits[c]);
        // a signed column shifted by half its range: its top bit turned over (the stored values keep the upper 16 bits of a deeper column)
        for (var c = 0; c < columns; c++)
        {
            if (!signed[c]) continue;
            var top = 1 << (bits[c] - 1);
            for (var i = 0; i < entries; i++) values[c][i] ^= top;
        }
        return new JpxPalette(values, bits, integers, depth, signed) { Reserved = reserved };
    }

    /// <summary>
    /// The entries of a column as codes with their bit depth: the depth Bi (up to 38) with all stored bits; with a fixed or floating point
    /// <paramref name="format"/> (M.11.7.8) each entry read as that number, as a level of 16 bits (a signed column: 2^15 + (2^15 − 1)·x,
    /// <see cref="JpxPixelFormat.SignedLevel"/>). For the Parameterized colour specification.
    /// </summary>
    public (long[] Codes, int Bits) Codes(int column, int format = 0)
    {
        var values = _values[column];
        var codes = new long[values.Length];
        if (format != 0)
        {
            if (!JpxPixelFormat.Fits(format, _depth[column])) throw new JpxException("JPX: Pixelformat passt nicht zur Bittiefe");
            for (var i = 0; i < values.Length; i++) codes[i] = Level(column, i, format);
            return (codes, 16);
        }
        // the stored values keep the upper 16 bits of a deeper column: shifted back up to its depth
        var shift = Math.Max(0, _depth[column] - _bits[column]);
        for (var i = 0; i < values.Length; i++) codes[i] = (long)values[i] << shift;
        return (codes, _depth[column]);
    }

    /// <summary>
    /// The entries of a column read as the numbers of a fixed or floating point <paramref name="format"/> (M.11.7.8), as they are, beyond 0 … 1
    /// too. For scRGB, whose numbers are linear light (<see cref="JpxParameterized.NumberCode"/>).
    /// </summary>
    public double[] Numbers(int column, int format)
    {
        if (!JpxPixelFormat.Fits(format, _depth[column])) throw new JpxException("JPX: Pixelformat passt nicht zur Bittiefe");
        var integers = _integers[column];
        var numbers = new double[integers.Length];
        for (var i = 0; i < numbers.Length; i++) numbers[i] = JpxPixelFormat.Value(integers[i], _depth[column], format);
        return numbers;
    }

    /// <summary>
    /// Returns a column as a table index → 8 bits: stretched, or through <paramref name="map"/> (raw value, bits, 0). With a fixed or floating
    /// point <paramref name="format"/> (M.11.7.8) each entry is read as that number first, as a level of 16 bits.
    /// </summary>
    public byte[] Table(int column, Func<int, int, int, byte>? map = null, int format = 0)
    {
        var values = _values[column];
        var bits = _bits[column];
        var table = new byte[values.Length];
        if (format != 0)
        {
            if (!JpxPixelFormat.Fits(format, _depth[column])) throw new JpxException("JPX: Pixelformat passt nicht zur Bittiefe");
            for (var i = 0; i < values.Length; i++)
            {
                var level = Level(column, i, format);
                table[i] = map?.Invoke(level, 16, 0) ?? JpxDecoder.Stretch(level, 16);
            }
            return table;
        }
        for (var i = 0; i < values.Length; i++)
            table[i] = map?.Invoke(values[i], bits, 0) ?? JpxDecoder.Stretch(values[i], bits);
        return table;
    }

    /// <summary>Entry <paramref name="i"/> of a column read as the number of a fixed or floating point <paramref name="format"/>, as 16 bits.</summary>
    private ushort Level(int column, int i, int format)
    {
        var value = JpxPixelFormat.Value(_integers[column][i], _depth[column], format);
        return _signed[column] ? JpxPixelFormat.SignedLevel(value) : JpxPixelFormat.Level(value, 65535);
    }
}
