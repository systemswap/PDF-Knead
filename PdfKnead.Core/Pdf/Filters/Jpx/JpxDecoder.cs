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

    /// <summary>sYCC: converted to RGB during decoding.</summary>
    Ycc,

    /// <summary>e-sYCC (PIMA 7667, Annex B): half the chroma of sYCC; converted to RGB during decoding, what lies outside sRGB clipped.</summary>
    EYcc,
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

    /// <summary>The white point of <see cref="JpxColor.Lab"/> (CIE XYZ, Y = 1); D50 unless the parameters name another illuminant.</summary>
    public double[] WhitePoint { get; init; } = [0.96422, 1, 0.82521];

    /// <summary>The codestream is incomplete or damaged; what could be decoded is shown.</summary>
    public bool Truncated { get; init; }

    /// <summary>Number of resolution levels by which the image was decoded smaller (width and height halved per level).</summary>
    public int Reduced { get; init; }

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
    /// <returns>The image.</returns>
    /// <exception cref="JpxException">The data contains no usable codestream, or the image is too large.</exception>
    public static JpxImage Decode(byte[] data, long maxPixels, bool applyPalette = true, Func<int, bool>? ownColour = null, double[]? indexDecode = null)
    {
        var p = DecodePlanes(data, maxPixels);
        return Assemble(p.Codestream, p.Boxes, p.Planes, p.PlaneWidth, p.PlaneHeight, p.PlaneX0, p.PlaneY0, p.Width, p.Height, p.Reduce,
            applyPalette, ownColour, p.Truncated, p.Budget, indexDecode);
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
            return (long)cs.Width * cs.Height * Math.Max(1, Math.Max(cs.Components.Length, cs.Output.Length)) * 3;
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

        var outW = (int)(JpxTile.CeilDiv(cs.X1, 1L << reduce) - JpxTile.CeilDiv(cs.X0, 1L << reduce));
        var outH = (int)(JpxTile.CeilDiv(cs.Y1, 1L << reduce) - JpxTile.CeilDiv(cs.Y0, 1L << reduce));
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
        var planeX0 = new int[planes.Length];
        var planeY0 = new int[planes.Length];
        for (var c = 0; c < planes.Length; c++)
        {
            var comp = cs.Output[c];
            planeX0[c] = (int)JpxTile.CeilDiv(JpxTile.CeilDiv(cs.X0, comp.Dx), 1L << reduce);
            planeY0[c] = (int)JpxTile.CeilDiv(JpxTile.CeilDiv(cs.Y0, comp.Dy), 1L << reduce);
            planeW[c] = (int)(JpxTile.CeilDiv(JpxTile.CeilDiv(cs.X1, comp.Dx), 1L << reduce) - planeX0[c]);
            planeH[c] = (int)(JpxTile.CeilDiv(JpxTile.CeilDiv(cs.Y1, comp.Dy), 1L << reduce) - planeY0[c]);
            var size = (long)planeW[c] * planeH[c];
            if (size > Array.MaxLength) throw new JpxException("JPX: Bild zu groß");
            state.Reserve(size * 2);
            planes[c] = new ushort[size];
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
            Store(cs, tile, planes, planeW, planeH, planeX0, planeY0, reduce);
            foreach (var tc in tile.Components) state.Release(tc.Data.Length * 4L);
        }

        return new JpxPlanes(cs, box, planes, planeW, planeH, planeX0, planeY0, outW, outH, reduce, state.Truncated || cs.Truncated) { Budget = state };
    }

    private static long Area(JpxCodestream cs, int reduce) =>
        (JpxTile.CeilDiv(cs.X1, 1L << reduce) - JpxTile.CeilDiv(cs.X0, 1L << reduce))
        * (JpxTile.CeilDiv(cs.Y1, 1L << reduce) - JpxTile.CeilDiv(cs.Y0, 1L << reduce));

    /// <summary>
    /// Component transformation, level shift, non-linearity, clamping; then copies the tile to its place in the image. With the array-based
    /// multiple component transformation of ISO/IEC 15444-2 (Annex J) the reconstructed components come from <see cref="JpxPart2.Transform"/>
    /// and no level shift applies (its offsets take the place of it, J.1); a non-linearity of Annex K follows on the reconstructed values.
    /// </summary>
    private static void Store(JpxCodestream cs, JpxTile tile, ushort[][] planes, int[] planeW, int[] planeH, int[] planeX0, int[] planeY0, int reduce)
    {
        var tcs = tile.Components;
        var own = tile.OwnHeader.Part2;
        if (JpxPart2.MultipleComponent(cs.Rsiz) && (tile.ComponentTransform & 2) != 0 && (own.Order ?? cs.Main.Part2.Order) is { Length: > 0 })
        {
            var first = tcs[0].Resolutions[tcs[0].Keep];
            var samples = first.Width * first.Height;
            var inputs = new float[tcs.Length][];
            for (var c = 0; c < tcs.Length; c++)
            {
                var r = tcs[c].Resolutions[tcs[c].Keep];
                if (r.Width != first.Width || r.Height != first.Height || tcs[c].Data.Length < samples) throw new JpxException("JPX: Komponenten verschieden groß");
                inputs[c] = tcs[c].Data.Length == samples ? tcs[c].Data : tcs[c].Data[..samples];
            }
            var outputs = JpxPart2.Transform(own, cs.Main.Part2, inputs, cs.Output.Length);
            for (var c = 0; c < outputs.Length; c++)
                Put(c, outputs[c], first, tcs[0], cs.Reconstructed[c], shift: 0);
            return;
        }
        if (tile.MultipleComponentTransform && tcs.Length >= 3)
        {
            var a = tcs[0].Resolutions[tcs[0].Keep];
            var b = tcs[1].Resolutions[tcs[1].Keep];
            var d = tcs[2].Resolutions[tcs[2].Keep];
            if (a.Width == b.Width && a.Width == d.Width && a.Height == b.Height && a.Height == d.Height)
                JpxWavelet.InverseColor(tcs[0].Data, tcs[1].Data, tcs[2].Data, a.Width * a.Height, tcs[0].Coding.Reversible);
        }
        for (var c = 0; c < tcs.Length && c < planes.Length; c++)
        {
            var comp = cs.Components[c];
            // the DC level shift of unsigned components (ISO/IEC 15444-1, G.1.2)
            Put(c, tcs[c].Data, tcs[c].Resolutions[tcs[c].Keep], tcs[c], comp, shift: comp.Signed ? 0 : 1 << (Math.Min(comp.Precision, 30) - 1));
        }

        // reconstructed component c (bit depth and sign of its input) → its plane; signed values are shown shifted by half the range
        void Put(int c, float[] data, JpxResolution res, JpxTileComponent geometry, JpxComponent input, int shift)
        {
            var output = cs.Output[c];
            var nlt = JpxPart2.Extended(cs.Rsiz) ? JpxPart2.NonLinearityOf(tile.OwnHeader.Part2, cs.Main.Part2, c) : null;
            var precision = Math.Min(output.Precision, 30);
            var max = (1 << precision) - 1;
            var display = output.Signed ? 1 << (precision - 1) : 0;
            var down = Math.Max(0, output.Precision - 16);
            // the reconstructed values within their own range, before a non-linearity
            var inBits = Math.Min(input.Precision, 30);
            long low = input.Signed ? -(1L << (inBits - 1)) : 0, high = input.Signed ? (1L << (inBits - 1)) - 1 : (1L << inBits) - 1;
            // Fewer levels than requested: this tile comes out larger and is subsampled
            var extra = reduce - (geometry.Coding.Levels - geometry.Keep);
            var plane = planes[c];
            int pw = planeW[c], ph = planeH[c];
            var y0 = planeY0[c];
            var x0 = planeX0[c];
            Rows(res.Height, (long)res.Width * res.Height, y =>
            {
                var py = ((res.Y0 + y) >> extra) - y0;
                if ((uint)py >= (uint)ph) return;
                var src = y * res.Width;
                var dst = py * pw;
                for (var x = 0; x < res.Width; x++)
                {
                    var px = ((res.X0 + x) >> extra) - x0;
                    if ((uint)px >= (uint)pw) continue;
                    long v = (long)MathF.Round(data[src + x]) + shift;
                    if (nlt is not null)
                        v = (long)Math.Round(JpxPart2.Apply(nlt, Math.Clamp(v, low, high), input.Precision, input.Signed));
                    v += display;
                    v = v < 0 ? 0 : v > max ? max : v;
                    plane[dst + px] = (ushort)(v >> down);
                }
            });
        }
    }

    private static JpxImage Assemble(JpxCodestream cs, JpxBoxes box, ushort[][] planes, int[] planeW, int[] planeH, int[] planeX0, int[] planeY0,
        int width, int height, int reduce, bool applyPalette, Func<int, bool>? ownColour, bool truncated, JpxDecodeState budget, double[]? indexDecode = null)
    {
        var precision = cs.Output.Select(c => Math.Min(16, c.Precision)).ToArray();
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
        var choice = applyPalette && (ownColour?.Invoke(n) ?? true) ? Choose(box.Colours, n) : default;
        var samples = new byte[count * n];
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
        if (color is JpxColor.Ycc or JpxColor.EYcc) YccToRgb(samples, width, height, color == JpxColor.EYcc ? 2 : 1);

        return new JpxImage
        {
            Width = width,
            Height = height,
            Channels = n,
            Samples = samples,
            Alpha = alpha,
            PremultipliedAlpha = premultiplied,
            Color = color is JpxColor.Ycc or JpxColor.EYcc ? JpxColor.Rgb : color,
            IccProfile = choice.Icc,
            WhitePoint = choice.White ?? Illuminant(0x00443530),
            Truncated = truncated,
            Reduced = reduce,
        };

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
                : channel.PaletteColumn >= 0 ? box.Palette!.Table(channel.PaletteColumn, map)
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
    /// PDF allows) and the two bi-level spaces. Map: per colour channel the raw value → 8 bits where the space needs more than stretching.
    /// </summary>
    internal static (JpxColor Color, byte[]? Icc, double[]? White, Func<int, int, int, int, byte>? Map) Choose(IReadOnlyList<JpxColourSpec> specs, int n)
    {
        foreach (var spec in specs)
        {
            switch (spec.Method)
            {
                case 1:
                    switch (spec.EnumCs, n)
                    {
                        case (16, 3): return (JpxColor.Rgb, null, null, null);                         // sRGB
                        case (17, 1): return (JpxColor.Gray, null, null, null);                        // sRGB-grey
                        case (18, 3): return (JpxColor.Ycc, null, null, null);                         // sYCC
                        case (24, 3): return (JpxColor.EYcc, null, null, null);                        // e-sYCC (PIMA 7667, Annex B; No. 944)
                        case (20, 3): return (JpxColor.Rgb, null, null, ESrgb);                        // e-sRGB
                        case (21, 3): return (JpxColor.Icc, CalibratedProfile.RommRgb(), null, null);  // ROMM-RGB
                        case (14, 3):                                                                  // CIELab
                            var (white, lab) = Lab(spec.Parameters);
                            return (JpxColor.Lab, null, white, lab);
                        case (12, 4): return (JpxColor.Cmyk, null, null, null);                        // CMYK
                        case (15, 1): return (JpxColor.Gray, null, null, null);                        // bi-level(2): 1 is white
                        case (0, 1): return (JpxColor.Gray, null, null, (_, v, b, _) => (byte)(255 - Stretch(v, b)));   // bi-level: 1 is black
                    }
                    break;
                case 2 or 3 when spec.Profile is { } icc && IccProfile.Parse(icc) is { } profile && profile.Channels == n:
                    return (JpxColor.Icc, icc, null, null);
            }
        }
        return (JpxColor.Unknown, null, null, null);
    }

    /// <summary>
    /// e-sRGB (PIMA 7667, cited in ISO/IEC 15444-2, Table M.25): n bits carry R′ = (C − 3·2^(n−3)) / (255·2^(n−9)), the non-linear sRGB value
    /// extended to about −0.75 … 1.25; shown as sRGB, what lies outside clipped (the sRGB curve maps 0 and 1 to themselves, so clipping before
    /// or after it is the same).
    /// </summary>
    private static byte ESrgb(int channel, int value, int bits, int down) =>
        (byte)Math.Clamp(Math.Round((value - 3 * Math.ScaleB(1.0, bits - 3)) / Math.ScaleB(1.0, bits - 9)), 0, 255);

    /// <summary>
    /// CIELab (ISO/IEC 15444-2, M.11.7.4.1): L* = (N − OL)·RL / MAX, a* and b* the same with their range and offset, MAX = 2^n − 1; without
    /// parameters the ranges 100, 170, 200 and the offsets 0, 2^(n−1), 2^(n−2) + 2^(n−3) under D50. Written in the encoding of
    /// <see cref="JpxColor.Lab"/>.
    /// </summary>
    private static (double[] White, Func<int, int, int, int, byte> Map) Lab(byte[]? ep)
    {
        uint[]? fields = ep is { Length: >= 24 } ? [.. Enumerable.Range(0, 6).Select(i => U32(ep, i * 4))] : null;
        var white = Illuminant(ep is { Length: >= 28 } ? U32(ep, 24) : 0x00443530);
        return (white, (k, value, bits, down) =>
        {
            k = Math.Min(k, 2);
            double range = fields?[2 * k] ?? k switch { 0 => 100, 1 => 170, _ => 200 };
            // given offsets count in the component's own bits; those above 16 bits came shifted down
            var offset = fields is not null ? fields[2 * k + 1] / Math.ScaleB(1.0, down)
                : k switch { 0 => 0, 1 => Math.ScaleB(1.0, bits - 1), _ => Math.ScaleB(1.0, bits - 2) + Math.ScaleB(1.0, bits - 3) };
            var v = (value - offset) * range / Math.Max(1, Math.ScaleB(1.0, bits) - 1);
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

    /// <summary>
    /// Converts sYCC (IEC 61966-2-1, Annex G) to RGB in place; with <paramref name="chroma"/> 2 e-sYCC (PIMA 7667, Annex B), which stores the
    /// colour differences at half their size, so that the extended range of e-sRGB fits — the way back uses twice the coefficients (B.2:
    /// 2.804, −0.6882, −1.4282, 3.544), the result is e-sRGB, shown as sRGB with what lies outside clipped (as <see cref="ESrgb"/>). Before
    /// e-sYCC was read as sYCC, its colours half as strong (docs/FEHLERGEDAECHTNIS.md, No. 944).
    /// </summary>
    private static void YccToRgb(byte[] s, int width, int height, int chroma = 1)
    {
        // Contributions per colour difference as tables (fixed point, 16 fractional bits), then clamped
        var crR = new int[256];
        var cbB = new int[256];
        var cbG = new int[256];
        var crG = new int[256];
        for (var v = 0; v < 256; v++)
        {
            var d = v - 128;
            crR[v] = (int)Math.Round(chroma * 1.402 * d * 65536);
            cbB[v] = (int)Math.Round(chroma * 1.772 * d * 65536);
            cbG[v] = (int)Math.Round(chroma * -0.344136 * d * 65536);
            crG[v] = (int)Math.Round(chroma * -0.714136 * d * 65536);
        }
        Rows(height, (long)width * height, row =>
        {
            for (int i = row * width * 3, end = i + width * 3; i < end; i += 3)
            {
                int y = s[i] << 16 | 0x8000, cb = s[i + 1], cr = s[i + 2];
                s[i] = Clamp(y + crR[cr]);
                s[i + 1] = Clamp(y + cbG[cb] + crG[cr]);
                s[i + 2] = Clamp(y + cbB[cb]);
            }
        });

        static byte Clamp(int v) => (byte)Math.Clamp(v >> 16, 0, 255);
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
internal sealed record JpxPlanes(JpxCodestream Codestream, JpxBoxes Boxes, ushort[][] Planes, int[] PlaneWidth, int[] PlaneHeight, int[] PlaneX0,
    int[] PlaneY0, int Width, int Height, int Reduce, bool Truncated)
{
    /// <summary>Memory budget of decoding; assembly continues to count against it.</summary>
    public JpxDecodeState Budget { get; init; } = null!;
}

/// <summary>One Colour Specification box (ISO/IEC 15444-1, I.5.3.3; ISO/IEC 15444-2, M.11.7.2).</summary>
/// <param name="Method">METH: 1 enumerated, 2 restricted ICC, 3 any ICC, 4 vendor, 5 parameterized.</param>
/// <param name="Precedence">PREC (signed).</param>
/// <param name="Approx">APPROX: 1 accurate … 4 poor; 0 in JP2.</param>
/// <param name="EnumCs">EnumCS of the enumerated method.</param>
/// <param name="Parameters">EP of the enumerated method (CIELab); null without.</param>
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
    private const uint Jp2c = 0x6A703263, Jp2h = 0x6A703268, Jplh = 0x6A706C68, Cgrp = 0x63677270, Colr = 0x636F6C72, Pclr = 0x70636C72,
        Cmap = 0x636D6170, Cdef = 0x63646566, Opct = 0x6F706374, Ftbl = 0x6674626C, Flst = 0x666C7374, Cref = 0x63726566;

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

    public static JpxBoxes Read(byte[] data)
    {
        var boxes = new JpxBoxes { Data = data, Start = 0, End = data.Length };
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0x4F) return boxes;
        // neither a codestream (SOC) nor a JP2/JPX file whose boxes name one: no image, as ISO 32000-2, 7.4.9 and the three reference readers
        // have it — before, the first 4 KB were searched for the start of a codestream behind other bytes or a damaged wrapper, and PDF-Knead
        // showed an image no other reader showed; no file of the collections needs it (S-A A11; docs/FEHLERGEDAECHTNIS.md, No. 939)
        if (!boxes.ReadFile(data)) throw new JpxException("JPX: kein Datenstrom");
        return boxes;
    }

    private static uint U32(byte[] d, int i) => (uint)(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3]);

    /// <summary>The boxes in [pos, end): type and contents; a length beyond the end is cut to it (damaged files).</summary>
    private static IEnumerable<(uint Type, int Start, int End)> Children(byte[] d, int pos, int end)
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
            if (length < header || pos + length > end) length = end - pos;
            yield return (type, pos + header, (int)(pos + length));
            pos = (int)(pos + length);
        }
    }

    /// <summary>Reads the top level; false if there is no codestream in it.</summary>
    private bool ReadFile(byte[] d)
    {
        var codestream = false;
        var header = new List<JpxColourSpec>();
        List<JpxColourSpec>? layer = null;
        List<(int, int, int)>? layerDefinitions = null;
        var firstLayer = true;
        var order = 0;
        foreach (var (type, s, e) in Children(d, 0, d.Length))
        {
            switch (type)
            {
                // the first codestream: in a Contiguous Codestream box, or in fragments listed by a Fragment Table box, which counts the same
                // (M.11.3); the JPX baseline file has its fragments in the file itself (M.9.2.5)
                case Jp2c when !codestream:
                    (Start, End, codestream) = (s, e, true);
                    break;
                case Ftbl when !codestream && Fragments(d, s, e) is { } joined:
                    (Data, Start, End, codestream) = (joined, 0, joined.Length, true);
                    break;
                case Jp2h:
                    foreach (var (inner, si, ei) in Children(d, s, e)) ReadHeaderBox(d, inner, si, ei, header, ref order);
                    break;
                // the first Compositing Layer Header box: its colour group replaces the colour specifications of the JP2 Header box, its channel
                // definition the default, and an Opacity box takes the place of both channel definitions (M.11.7, M.9.2.2)
                case Jplh when firstLayer:
                    firstLayer = false;
                    foreach (var (child, cs, ce) in Children(d, s, e))
                    {
                        // a Cross-Reference box stands for the box its fragments make up: Rtyp its type, the fragments its contents (of a
                        // superbox: its boxes) — within the file only (M.9.2.6), never another Cross-Reference box (M.11.4; No. 949)
                        var (inner, b, si, ei) = (child, d, cs, ce);
                        if (child == Cref)
                        {
                            if (ce - cs < 4 || U32(d, cs) == Cref || Fragments(d, cs + 4, ce) is not { } referenced) continue;
                            (inner, b, si, ei) = (U32(d, cs), referenced, 0, referenced.Length);
                        }
                        switch (inner)
                        {
                            case Cgrp:
                                layer ??= [];
                                foreach (var (colr, sc, ec) in Children(b, si, ei))
                                    if (colr == Colr && ColourSpec(b, sc, ec, order++) is { } spec) layer.Add(spec);
                                break;
                            case Opct when ei > si:
                                OpacityType = b[si];
                                break;
                            case Cdef:
                                layerDefinitions = Channels(b, si, ei);
                                break;
                        }
                    }
                    break;
                default:
                    // misplaced at the top level by some producers: read like inside the JP2 Header box
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
                break;
            case Cmap:
                Mapping = [];
                for (var i = s; i + 4 <= e; i += 4) Mapping.Add((d[i] << 8 | d[i + 1], d[i + 2], d[i + 3]));
                break;
            case Cdef:
                Definitions = Channels(d, s, e);
                break;
        }
    }

    /// <summary>A Colour Specification box; null if it is too short to say anything.</summary>
    private static JpxColourSpec? ColourSpec(byte[] d, int s, int e, int order)
    {
        if (e - s < 3) return null;
        int method = d[s], precedence = (sbyte)d[s + 1], approx = d[s + 2];
        return method switch
        {
            1 when e - s >= 7 => new JpxColourSpec(1, precedence, approx, U32(d, s + 3), e - s > 7 ? d[(s + 7)..e] : null, null, order),
            2 or 3 when e - s > 3 => new JpxColourSpec(method, precedence, approx, 0, null, d[(s + 3)..e], order),
            _ => new JpxColourSpec(method, precedence, approx, 0, null, null, order),   // not understood: kept in order, never chosen
        };
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
    /// JPX baseline keeps them in the file, M.9.2.5, M.9.2.6) or outside the data.
    /// </summary>
    private static byte[]? Fragments(byte[] d, int s, int e)
    {
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
                if (reference != 0 || offset > (ulong)d.Length || offset + length > (ulong)d.Length) return null;
                parts[i] = ((int)offset, (int)length);
                total += length;
            }
            // the fragments are parts of this file: together never larger than it (crafted lists: the same large range many times over)
            if (total == 0 || total > d.Length) return null;
            var joined = new byte[total];
            var at = 0;
            foreach (var (offset, length) in parts)
            {
                Array.Copy(d, offset, joined, at, length);
                at += length;
            }
            return joined;
        }
        return null;
    }
}

/// <summary>Palette (pclr): one value per column for each entry, any bit depth up to 38 (stored with at most 16 bits).</summary>
internal sealed class JpxPalette
{
    private readonly int[][] _values;
    private readonly int[] _bits;

    private JpxPalette(int[][] values, int[] bits)
    {
        _values = values;
        _bits = bits;
    }

    public int Columns => _bits.Length;

    public static JpxPalette? Read(byte[] d, int s, int e)
    {
        if (e - s < 3) return null;
        var entries = Math.Min(1024, d[s] << 8 | d[s + 1]);
        var columns = d[s + 2];
        if (entries == 0 || columns == 0 || e - s < 3 + columns) return null;
        var bits = new int[columns];
        for (var c = 0; c < columns; c++) bits[c] = (d[s + 3 + c] & 0x7F) + 1;
        var values = new int[columns][];
        for (var c = 0; c < columns; c++) values[c] = new int[entries];
        var p = s + 3 + columns;
        for (var i = 0; i < entries; i++)
        for (var c = 0; c < columns; c++)
        {
            var bytes = (bits[c] + 7) / 8;
            long v = 0;
            for (var b = 0; b < bytes; b++) v = v << 8 | (uint)(p < e ? d[p++] : 0);
            values[c][i] = (int)(bits[c] > 16 ? v >> (bits[c] - 16) : v);
        }
        for (var c = 0; c < columns; c++) bits[c] = Math.Min(16, bits[c]);
        return new JpxPalette(values, bits);
    }

    /// <summary>Returns a column as a table index → 8 bits: stretched, or through <paramref name="map"/> (raw value, bits, 0).</summary>
    public byte[] Table(int column, Func<int, int, int, byte>? map = null)
    {
        var values = _values[column];
        var bits = _bits[column];
        var table = new byte[values.Length];
        for (var i = 0; i < values.Length; i++)
            table[i] = map?.Invoke(values[i], bits, 0) ?? JpxDecoder.Stretch(values[i], bits);
        return table;
    }
}
