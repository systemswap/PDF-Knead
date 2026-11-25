using System.IO.Compression;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Filters.Jpeg;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Blacks out the pixels of an image under redaction areas and writes it anew, losslessly and in its own samples: colour space, bit
/// depth, /Decode, rendering intent, masks and the full resolution stay as they are; only the samples the areas touch change (ISO
/// 32000-2, 8.9.5). Every pixel an area merely touches is blacked out, not only those whose centre it covers (see
/// docs/FEHLERGEDAECHTNIS.md, No. 36).
/// </summary>
/// <remarks>
/// Coarse images — one pixel larger than <see cref="RefineAbove"/> on the page, a 1 × 1 colour stretched over a whole box, a 6 × 1
/// gradient — are first refined by whole factors to pixels of at most <see cref="Tolerance"/>: otherwise a bar that touches one pixel
/// blacks out the whole pixel far beyond itself (a 200 × 100 pt image went entirely black). Interpolated images (/Interpolate) are
/// refined bilinearly, like a reader enlarges them, the others by repeating each pixel. Images larger than 16 megapixels are no longer
/// reduced on the way (the decoder for the display reduces them; written back, the whole image lost its resolution). See
/// docs/FEHLERGEDAECHTNIS.md, No. 170.
/// </remarks>
internal static class ImageBlanker
{
    /// <summary>Size of an image pixel on the page (in points) that refining a coarse image aims at.</summary>
    internal const double Tolerance = 0.25;

    /// <summary>A refined image has at most this many pixels (beyond that the refinement stays coarser): 16 megapixels.</summary>
    internal const long MaxRefinedPixels = 16_000_000;

    /// <summary>
    /// What refining may add on one page together: 64 megapixels. A small image drawn many times on a page (a damaged file) was refined each
    /// time up to <see cref="MaxRefinedPixels"/>, and drawing the redacted page took 4 GB (docs/FEHLERGEDAECHTNIS.md, No. 378). Beyond it the
    /// images stay coarser — their black reaches further beyond the bar, never less far.
    /// </summary>
    internal const long PageRefineBudget = 64_000_000;

    /// <summary>
    /// Samples redacting may read and write on one page together, of all images and masks: eight gigasamples — a hundred images of 80
    /// megapixels. A damaged page drew a 2 × 2 image three thousand times with a mask of 75 megapixels, blacked out anew for each drawing,
    /// and redacting it took over twenty seconds (docs/FEHLERGEDAECHTNIS.md, No. 388). An image beyond it goes, as one that cannot be read:
    /// nothing of it stays under a bar.
    /// </summary>
    internal const long PageSamples = 8_000_000_000;

    /// <summary>
    /// The samples reading an image takes, with its masks (/SMask, a /Mask stream): width × height of each — what decoding has to go
    /// through whatever size it is reduced to (docs/FEHLERGEDAECHTNIS.md, No. 463).
    /// </summary>
    /// <param name="image">The image stream.</param>
    /// <returns>The samples; unreadable sizes count as nothing.</returns>
    internal static long StoredSamples(PdfStream image)
    {
        static long Of(PdfDictionary dict) =>
            Math.Max(0, (long)(dict.Get(PdfName.Get("Width")).AsNumber() ?? 0)) * Math.Max(0, (long)(dict.Get(PdfName.Get("Height")).AsNumber() ?? 0));
        var samples = Of(image.Dictionary);
        if (image.Dictionary.Get(PdfName.Get("SMask")) is PdfStream soft) samples += Of(soft.Dictionary);
        if (image.Dictionary.Get(PdfName.Get("Mask")) is PdfStream mask) samples += Of(mask.Dictionary);
        return samples;
    }

    /// <summary>The work left on a page, shared by every image redacted there: refining (<see cref="PageRefineBudget"/>) and samples
    /// (<see cref="PageSamples"/>).</summary>
    /// <param name="samples">The samples the page may read and write (<see cref="PageSamples"/>).</param>
    internal sealed class PageBudget(long samples = PageSamples)
    {
        /// <summary>Pixels refining may still add.</summary>
        public long Left { get; set; } = PageRefineBudget;

        private long _samples = samples;

        /// <summary>Takes the samples of an image from the budget; false if they are no longer there (the image then goes).</summary>
        public bool TakeSamples(long samples)
        {
            if (samples > _samples) return false;
            _samples -= samples;
            return true;
        }
    }

    /// <summary>
    /// Only images whose pixels are larger than this on the page (in points) are refined — really coarse ones (Stufe A, decision of the
    /// user, 12.11.2026: „nur ein wirklich grobes Bild, ein Bildpunkt über etwa 1–2 pt“; before 1 pt). Readers draw them as blocks, so
    /// repeating each pixel keeps their look. Finer images stay as they are, losslessly in their own samples: smoothed by the reader, a refined
    /// copy looks slightly different everywhere beside the bar (a CMYK photo with pixels of 1.25 pt was refined fivefold and came out changed
    /// at its edges, DocLayNet 200ba8a6), and the black reaches at most one pixel — under 2 pt — beyond the bar, never less far.
    /// </summary>
    internal const double RefineAbove = 2.0;

    private enum Role
    {
        /// <summary>An ordinary image: blacked out (the darkest value of its colour space).</summary>
        Color,

        /// <summary>A stencil mask (/ImageMask): under the areas nothing is painted any more.</summary>
        Stencil,

        /// <summary>A soft mask (/SMask): opaque, so that the blacked-out colour shows like the bar.</summary>
        SoftMask,

        /// <summary>An explicit mask (/Mask stream): painted, for the same reason.</summary>
        ExplicitMask,
    }

    // Entries that do not fit the new data or could carry the old content (an alternate image, a thumbnail in the XMP metadata, an
    // associated file with the original; docs/FEHLERGEDAECHTNIS.md, No. 434)
    private static readonly string[] Dropped =
        ["Filter", "DecodeParms", "Length", "DL", "F", "FFilter", "FDecodeParms", "SMask", "SMaskInData", "ID", "Width", "Height", "BitsPerComponent",
         .. Redactor.DescribesOldContent];

    /// <summary>
    /// Blacks out an image XObject in its own samples.
    /// </summary>
    /// <param name="doc">The document (stream data, colour spaces).</param>
    /// <param name="stream">The image.</param>
    /// <param name="resources">Resources it was drawn with (colour spaces by name).</param>
    /// <param name="imageToPage">Unit square of the image → page.</param>
    /// <param name="areas">The redaction areas in page coordinates.</param>
    /// <param name="handled">
    /// False if the image cannot be read in its own samples (unusual bit depth, a JPEG the own decoder does not read): the caller then
    /// blacks it out another way.
    /// </param>
    /// <returns>The new image; null if it is handled but unreadable (the caller drops it).</returns>
    public static PdfStream? TryBlank(PdfDocument doc, PdfStream stream, PdfDictionary? resources, Matrix imageToPage,
        IReadOnlyList<RectD> areas, out bool handled, PageBudget? budget = null)
    {
        var stencil = stream.Dictionary.GetBoolean(PdfName.Get("ImageMask")) == true;
        var interpolate = stream.Dictionary.GetBoolean(PdfName.Get("Interpolate")) == true;
        try
        {
            return Blank(doc, stream, resources, imageToPage, areas, stencil ? Role.Stencil : Role.Color, interpolate, out handled, budget);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            // damaged values no check anticipated (found by fuzzing): the image goes the other way, as decoded for the display
            handled = false;
            return null;
        }
    }

    private static PdfStream? Blank(PdfDocument doc, PdfStream stream, PdfDictionary? resources, Matrix imageToPage,
        IReadOnlyList<RectD> areas, Role role, bool interpolate, out bool handled, PageBudget? budget = null)
    {
        handled = false;
        var dict = stream.Dictionary;
        var width = (int)(dict.Get(PdfName.Get("Width")).AsNumber() ?? 0);
        var height = (int)(dict.Get(PdfName.Get("Height")).AsNumber() ?? 0);
        var maskLike = role is Role.Stencil or Role.ExplicitMask || dict.GetBoolean(PdfName.Get("ImageMask")) == true;
        var bpc = maskLike ? 1 : (int)(dict.Get(PdfName.Get("BitsPerComponent")).AsNumber() ?? 8);
        if (width <= 0 || height <= 0 || width > PdfImageDecoder.MaxSide || height > PdfImageDecoder.MaxSide ||
            (long)width * height > PdfImageDecoder.MaxPixels || !ImageDepth.Readable(bpc))
            return null;   // nonsensical: the decoder does not draw it either — dropped
        if (maskLike && role != Role.ExplicitMask) role = Role.Stencil;

        ColorSpace? space = null;
        if (!maskLike)
        {
            var spaceObj = dict.Get(PdfName.Get("ColorSpace"));
            try
            {
                space = spaceObj is PdfNull ? ColorSpace.DeviceGray : ColorSpace.Create(spaceObj, resources, doc);
            }
            catch (PdfException)
            {
                return null;
            }
        }
        var n = Math.Max(space?.Components ?? 1, 1);
        var decode = PdfFunction.Numbers(dict.Get(PdfName.Get("Decode")) as PdfArray);

        // the masks first: one that cannot be read here makes the whole image go the other way
        PdfStream? softMask = null, explicitMask = null;
        if (role == Role.Color)
        {
            if (dict.Get(PdfName.Get("SMask")) is PdfStream sm)
            {
                softMask = Blank(doc, sm, resources, imageToPage, areas, Role.SoftMask, interpolate, out var ok, budget);
                if (!ok) return null;
                if (softMask is null) return Unreadable(out handled);
            }
            if (dict.Get(PdfName.Get("Mask")) is PdfStream em)
            {
                explicitMask = Blank(doc, em, resources, imageToPage, areas, Role.ExplicitMask, interpolate, out var ok, budget);
                if (!ok) return null;
                if (explicitMask is null) return Unreadable(out handled);
            }
        }

        if (budget is not null && !budget.TakeSamples((long)width * height)) return Unreadable(out handled);
        Source? source;
        try
        {
            source = Open(doc, stream, width, height, bpc, n, maskLike);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or OverflowException)
        {
            return Unreadable(out handled);
        }
        if (source is null) return null;   // not in its own samples: the other way
        handled = true;
        // a depth between the allowed ones (read leniently, S-A A192): blacked out and written with 8 or 16 bits, the same appearance — the
        // values scaled, the indices of an Indexed space kept (docs/FEHLERGEDAECHTNIS.md, No. 1134)
        int? widenedFrom = null;
        if (!ImageDepth.Allowed(source.Bpc))
        {
            var indexed = space is { IsIndexed: true };
            widenedFrom = source.Bpc;
            decode = ImageDepth.Decode(decode, source.Bpc, indexed);
            source = new Source(source.Width, source.Height, ImageDepth.Wider(source.Bpc),
                new ImageDepth.WideRows(source.Rows, source.Width * n, source.Bpc, indexed));
        }
        using (source)
        {
            (width, height, bpc) = (source.Width, source.Height, source.Bpc);
            if (stream.Dictionary.Get(PdfName.Filter) is var filters &&
                (filters as PdfName ?? (filters is PdfArray { Count: > 0 } list ? list.Get(list.Count - 1) as PdfName : null)) is { Value: "JPXDecode" })
            {
                // JPEG 2000: /Decode counts with a /ColorSpace of the image that fits it (for /Indexed the decoder has mapped the indices with
                // it already, the copy does without it), not where the colour space comes from the codestream (ISO 32000-2, 7.4.9;
                // docs/FEHLERGEDAECHTNIS.md, No. 739, 945) — as when drawing
                if (source.OwnSpace is not null || space is { IsIndexed: true }) decode = null;
                if (source.OwnSpace is { } own)
                {
                    n = own.Components;
                    try
                    {
                        space = ColorSpace.Create(own.Space, null, doc);
                    }
                    catch (PdfException)
                    {
                        return null;
                    }
                }
                if (source.Alpha is { } alpha && softMask is null)
                {
                    var alphaImage = new PdfStream(Dict(("Type", PdfName.Get("XObject")), ("Subtype", PdfName.Get("Image")), ("Width", PdfInteger.Of(width)),
                        ("Height", PdfInteger.Of(height)), ("ColorSpace", PdfName.Get("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8))), alpha);
                    softMask = Blank(doc, alphaImage, resources, imageToPage, areas, Role.SoftMask, interpolate, out var ok, budget);
                    if (!ok || softMask is null) return null;
                }
            }
            var blank = BlankValue(role, space, n, bpc, decode);
            // an image with a pre-multiplied soft mask (/Matte) and its mask must keep the same size (ISO 32000-2, 11.6.5.2, Table 143): refined
            // one after the other, the page budget could leave the image coarser than its mask; neither is refined then
            // (docs/FEHLERGEDAECHTNIS.md, No. 440)
            var matte = role == Role.SoftMask ? dict.ContainsKey(PdfName.Get("Matte"))
                : role == Role.Color && dict.Get(PdfName.Get("SMask")) is PdfStream withMatte && withMatte.Dictionary.ContainsKey(PdfName.Get("Matte"));
            var (kx, ky) = matte ? (1, 1) : Factors(imageToPage, width, height, budget);
            var smooth = interpolate && bpc >= 8 && !maskLike && space is not { IsIndexed: true } && (kx > 1 || ky > 1);
            var (w, h) = (width * kx, height * ky);
            if (Quads(imageToPage, w, h, areas) is not { } quads) return null;

            var packed = new MemoryStream();
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
                Write(source, z, width, height, n, bpc, kx, ky, smooth, quads, blank, missing: maskLike ? (byte)0xFF : (byte)0);

            var copy = new PdfDictionary { Resolver = dict.Resolver };
            foreach (var (key, value) in dict.Entries)
                if (Array.IndexOf(Dropped, key.Value) < 0 && !(key.Value == "Mask" && explicitMask is not null)) copy.Set(key, value);
            copy.Set(PdfName.Type, PdfName.Get("XObject"));
            copy.Set(PdfName.Subtype, PdfName.Get("Image"));
            copy.Set(PdfName.Get("Width"), PdfInteger.Of(w));
            copy.Set(PdfName.Get("Height"), PdfInteger.Of(h));
            copy.Set(PdfName.Get("BitsPerComponent"), PdfInteger.Of(bpc));
            copy.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
            // JPEG 2000 in the colour space of its data: /Decode was ignored there (ISO 32000-2, 7.4.9) and must not count now (No. 739)
            if (source.OwnSpace is { } ownSpace) copy.Set(PdfName.Get("ColorSpace"), ownSpace.Space);
            if (decode is null || source.OwnSpace is not null) copy.Remove(PdfName.Get("Decode"));
            if (softMask is not null) copy.Set(PdfName.Get("SMask"), softMask);
            if (explicitMask is not null) copy.Set(PdfName.Get("Mask"), explicitMask);
            if (widenedFrom is { } narrow) ImageDepth.Describe(copy, narrow, space is { IsIndexed: true });
            return new PdfStream(copy, packed.ToArray());
        }
    }

    private static PdfStream? Unreadable(out bool handled)
    {
        handled = true;
        return null;
    }

    /// <summary>Size of an image pixel on the page in points, along the image's width and height.</summary>
    internal static (double X, double Y) PixelSize(Matrix imageToPage, int width, int height) =>
        (Math.Sqrt(imageToPage.A * imageToPage.A + imageToPage.B * imageToPage.B) / width,
         Math.Sqrt(imageToPage.C * imageToPage.C + imageToPage.D * imageToPage.D) / height);

    /// <summary>
    /// Refinement factors: for an image whose pixels are larger than <see cref="RefineAbove"/>, an image pixel no larger than
    /// <see cref="Tolerance"/> on the page in either direction, the refined image at most <see cref="MaxRefinedPixels"/> (otherwise as fine
    /// as that allows); (1, 1) for finer images.
    /// </summary>
    /// <param name="budget">The refining left on the page; the refinement chosen is taken from it. Null: only the limit per image.</param>
    internal static (int Kx, int Ky) Factors(Matrix imageToPage, int width, int height, PageBudget? budget = null)
    {
        var (kx, ky) = Factors(imageToPage, width, height, budget is null ? MaxRefinedPixels : Math.Min(MaxRefinedPixels, budget.Left));
        if (budget is not null && (kx > 1 || ky > 1)) budget.Left -= (long)width * height * kx * ky;
        return (kx, ky);
    }

    private static (int Kx, int Ky) Factors(Matrix imageToPage, int width, int height, long maxPixels)
    {
        var (px, py) = PixelSize(imageToPage, width, height);
        if (!(Math.Max(px, py) > RefineAbove) || maxPixels <= (long)width * height) return (1, 1);
        double kx = px > Tolerance ? Math.Ceiling(px / Tolerance) : 1, ky = py > Tolerance ? Math.Ceiling(py / Tolerance) : 1;
        if (!double.IsFinite(kx) || !double.IsFinite(ky)) return (1, 1);
        kx = Math.Min(kx, PdfImageDecoder.MaxSide / width);
        ky = Math.Min(ky, PdfImageDecoder.MaxSide / height);
        var area = (double)width * height;
        if (area * kx * ky > maxPixels)
        {
            var f = Math.Sqrt(Math.Max(maxPixels, area) / (area * kx * ky));
            (kx, ky) = (Math.Max(1, Math.Floor(kx * f)), Math.Max(1, Math.Floor(ky * f)));
            while (area * kx * ky > maxPixels && (kx > 1 || ky > 1))
                if (kx >= ky) kx = Math.Max(1, Math.Floor(kx * 0.9));
                else ky = Math.Max(1, Math.Floor(ky * 0.9));
        }
        return ((int)Math.Max(1, kx), (int)Math.Max(1, ky));
    }

    /// <summary>The areas as quadrilaterals in pixels of an image of <paramref name="width"/> × <paramref name="height"/> (row 0 at the top); null if the image has no extent.</summary>
    internal static List<(double X, double Y)[]>? Quads(Matrix imageToPage, int width, int height, IReadOnlyList<RectD> areas)
    {
        Matrix pageToImage;
        try
        {
            pageToImage = imageToPage.Invert();
        }
        catch (ArgumentException)
        {
            return null;
        }
        var quads = new List<(double X, double Y)[]>(areas.Count);
        foreach (var r in areas)
        {
            var quad = new (double X, double Y)[4];
            var corners = new[] { (r.Left, r.Top), (r.Right, r.Top), (r.Right, r.Bottom), (r.Left, r.Bottom) };
            for (var i = 0; i < 4; i++)
            {
                var (u, v) = pageToImage.Transform(corners[i].Item1, corners[i].Item2);
                quad[i] = (u * width, (1 - v) * height);
            }
            if (quad.Max(p => p.X) <= 0 || quad.Min(p => p.X) >= width || quad.Max(p => p.Y) <= 0 || quad.Min(p => p.Y) >= height) continue;
            quads.Add(quad);
        }
        return quads;
    }

    /// <summary>
    /// Calls <paramref name="blank"/> for the pixels [x0, x1) of row <paramref name="y"/> that one of the (convex) quadrilaterals
    /// overlaps with positive area: the extent of the quadrilateral within the row band, without allocation.
    /// </summary>
    internal static void Spans(List<(double X, double Y)[]> quads, int y, int width, Action<int, int> blank)
    {
        const double eps = 1e-6;
        double y0 = y, y1 = y + 1;
        foreach (var quad in quads)
        {
            double minX = double.MaxValue, maxX = double.MinValue, lo = double.MaxValue, hi = double.MinValue;
            for (var i = 0; i < 4; i++)
            {
                var (ax, ay) = quad[i];
                if (ay >= y0 && ay <= y1)
                {
                    (minX, maxX) = (Math.Min(minX, ax), Math.Max(maxX, ax));
                    (lo, hi) = (Math.Min(lo, ay), Math.Max(hi, ay));
                }
                var (bx, by) = quad[(i + 1) % 4];
                foreach (var edge in (ReadOnlySpan<double>)[y0, y1])
                {
                    if ((ay - edge) * (by - edge) >= 0) continue;
                    var x = ax + (edge - ay) * (bx - ax) / (by - ay);
                    (minX, maxX) = (Math.Min(minX, x), Math.Max(maxX, x));
                    (lo, hi) = (Math.Min(lo, edge), Math.Max(hi, edge));
                }
            }
            if (hi - lo <= eps || maxX - minX <= eps) continue;
            var from = (int)Math.Max(0, Math.Floor(minX + eps));
            var to = (int)Math.Min(width, Math.Ceiling(maxX - eps));
            if (to > from) blank(from, to);
        }
    }

    /// <summary>
    /// Refines interleaved 8-bit pixels (<paramref name="bpp"/> bytes each) by whole factors: repeated, or bilinear like a reader
    /// enlarging an interpolated image. Always a new array.
    /// </summary>
    internal static byte[] Refine(byte[] pixels, int width, int height, int bpp, int kx, int ky, bool smooth)
    {
        if (kx == 1 && ky == 1) return (byte[])pixels.Clone();
        var (w, h) = (width * kx, height * ky);
        var result = new byte[(long)w * h * bpp];
        var (x0, x1, fx) = Weights(width, kx);
        for (var y = 0; y < h; y++)
        {
            var row = result.AsSpan(y * w * bpp, w * bpp);
            if (!smooth)
            {
                var source = pixels.AsSpan(y / ky * width * bpp, width * bpp);
                for (var x = 0; x < w; x++) source.Slice(x / kx * bpp, bpp).CopyTo(row.Slice(x * bpp, bpp));
                continue;
            }
            var (r0, r1, fy) = Weight(y, height, ky);
            var a = pixels.AsSpan(r0 * width * bpp, width * bpp);
            var b = pixels.AsSpan(r1 * width * bpp, width * bpp);
            for (var x = 0; x < w; x++)
                for (var c = 0; c < bpp; c++)
                    row[x * bpp + c] = (byte)Math.Round(Mix(a[x0[x] * bpp + c], a[x1[x] * bpp + c], b[x0[x] * bpp + c], b[x1[x] * bpp + c], fx[x], fy));
        }
        return result;
    }

    private static double Mix(double a0, double a1, double b0, double b1, double fx, double fy) =>
        (1 - fy) * (a0 + (a1 - a0) * fx) + fy * (b0 + (b1 - b0) * fx);

    /// <summary>Source pixels and weight for each refined column: pixel centres, clamped at the edges.</summary>
    private static (int[] X0, int[] X1, double[] Fx) Weights(int width, int kx)
    {
        var w = width * kx;
        var (x0, x1, fx) = (new int[w], new int[w], new double[w]);
        for (var x = 0; x < w; x++) (x0[x], x1[x], fx[x]) = Weight(x, width, kx);
        return (x0, x1, fx);
    }

    private static (int A, int B, double F) Weight(int refined, int size, int k)
    {
        var s = (refined + 0.5) / k - 0.5;
        if (s <= 0) return (0, 0, 0);
        if (s >= size - 1) return (size - 1, size - 1, 0);
        var a = (int)Math.Floor(s);
        return (a, a + 1, s - a);
    }

    // -------------------------------------------------- own samples

    /// <summary>Rows of raw samples of an image, after all filters.</summary>
    private sealed class Source(int width, int height, int bpc, IRowSource rows) : IDisposable
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int Bpc { get; } = bpc;
        public IRowSource Rows { get; } = rows;

        /// <summary>JPEG 2000: the colour space of the decoded channels when the image has none of its own that fits them (written into the
        /// new image), with their number; null otherwise.</summary>
        public (PdfObject Space, int Components)? OwnSpace { get; init; }

        /// <summary>JPEG 2000 with /SMaskInData: the opacity from the codestream (8 bits per pixel), to become the new image's soft mask.</summary>
        public byte[]? Alpha { get; init; }

        public void Dispose() => (Rows as IDisposable)?.Dispose();
    }

    /// <summary>
    /// The raw samples: unfiltered or Flate row by row, other filters (LZW, CCITT …) at once, JBIG2 and JPEG 2000 through the own decoders,
    /// JPEG through the own decoder at full size in its own channels — grey, RGB and CMYK as the colour space has them (Stufe A, decision of
    /// the user, 12.11.2026: before, a CMYK JPEG went the other way and came back as RGB, refined far beyond the bar). Null if they cannot be
    /// had in the image's own colour space.
    /// </summary>
    private static Source? Open(PdfDocument doc, PdfStream stream, int width, int height, int bpc, int n, bool maskLike)
    {
        var rowBytesLong = ((long)width * n * bpc + 7) / 8;
        if (rowBytesLong > int.MaxValue / 2) return null;
        var rowBytes = (int)rowBytesLong;
        if (ImageRows.Open(doc, stream, rowBytes) is { } rows) return new Source(width, height, bpc, rows);
        var decoded = doc.DecodeStream(stream, (int)Math.Min(int.MaxValue / 2, rowBytesLong * height));
        switch (decoded.ImageFilter?.Value)
        {
            case null:
                return new Source(width, height, bpc, new ImageRowsArray(decoded.Data, rowBytes, maskLike ? (byte)0xFF : (byte)0));
            case "JBIG2Decode" when bpc == 1 && n == 1:
                return new Source(width, height, bpc, new ImageRowsArray(PdfImageDecoder.Jbig2(decoded, doc, width, height).Data, rowBytes));
            // at full size only within the budget of the machine (an A0 scan can take gigabytes; No. 743; the decoder checks it before it takes
            // memory), otherwise as the display draws it
            case "DCTDecode" when !maskLike && bpc == 8 && Jpeg(stream, decoded, n) is { } j:
                return new Source(j.Width, j.Height, 8, new ImageRowsArray(j.Samples, j.Width * j.Channels));
            case "JPXDecode" when !maskLike:
                return Jpx(doc, stream, decoded.Data, n);
            default:
                return null;
        }
    }

    /// <summary>
    /// JPEG 2000 through the own decoder at full size (up to <see cref="PdfImageDecoder.MaxPixels"/>; larger ones a resolution level smaller, as
    /// the display draws them): its channels with 8 bits each, in the image's colour space if it has one that fits them, otherwise in the
    /// one the codestream gives (ICC profile; grey, RGB or CMYK by the number of channels) — written into the new image. With /SMaskInData its
    /// opacity becomes the soft mask. Before, JPEG 2000 went through the display: converted to RGB and reduced above 16 megapixels.
    /// </summary>
    private static Source? Jpx(PdfDocument doc, PdfStream stream, byte[] data, int n) =>
        DecodeJpx(stream, data, n) is not { } d ? null
            : new Source(d.Image.Width, d.Image.Height, 8, new ImageRowsArray(d.Image.Samples, d.Image.Width * d.Image.Channels)) { OwnSpace = d.OwnSpace, Alpha = d.Alpha };

    /// <summary>
    /// A JPEG 2000 image in its own channels, 8 bits each, at full size (up to <see cref="PdfImageDecoder.MaxPixels"/>): with the colour space
    /// to write when the image's own (<paramref name="n"/> components; 0 if it has none) does not fit them, and the opacity of /SMaskInData.
    /// Null if it cannot be decoded. Shared by blacking out and cutting (<see cref="ImageCut.CutEncoded"/>).
    /// </summary>
    internal static (JpxImage Image, (PdfObject Space, int Components)? OwnSpace, byte[]? Alpha)? DecodeJpx(PdfStream stream, byte[] data, int n)
    {
        var dict = stream.Dictionary;
        var spaceObj = dict.Get(PdfName.Get("ColorSpace"));
        var indexed = spaceObj is PdfArray { Count: > 0 } a && a.Get(0) is PdfName { Value: "Indexed" or "I" };
        // at full size it must fit into the memory: a header claiming a huge image (or many large images redacted at once) would otherwise
        // end in running out of memory (docs/FEHLERGEDAECHTNIS.md, No. 403); then null — the image goes the other way, as the display draws it
        if (JpxDecoder.FullSizeBytes(data) is not { } needed || needed > PdfLimits.Budget(4L << 30)) return null;
        JpxImage jpx;
        try
        {
            // a /ColorSpace of the image that fits the channels determines them (ISO 32000-2, 7.4.9), as when drawing
            // /SMaskInData for data without a channel definition, channels no colour fits: as when drawing (docs/FEHLERGEDAECHTNIS.md, No. 996)
            int? declared = spaceObj is PdfNull ? null : n;
            jpx = JpxDecoder.Decode(data, PdfImageDecoder.MaxPixels, applyPalette: !indexed, channels => spaceObj is PdfNull || n != channels,
                indexed ? PdfFunction.Numbers(dict.Get(PdfName.Get("Decode")) as PdfArray) : null, PdfImageDecoder.SMaskInData(dict), declared).Fitted(declared);
        }
        catch (Exception e) when (e is JpxException or IndexOutOfRangeException or ArgumentException or OverflowException or AggregateException or InvalidOperationException)
        {
            return null;   // the display does not draw it either: dropped by the caller as unreadable
        }
        if (jpx.Channels is < 1 or > 32 || jpx.Samples.Length < (long)jpx.Width * jpx.Height * jpx.Channels) return null;
        (PdfObject, int)? own = null;
        if (spaceObj is PdfNull || n != jpx.Channels)
        {
            // the colour space the decoder gives (ICC profile, CIELab), otherwise by the number of channels — the same as when drawing
            var space = jpx.SpaceObject()
                ?? jpx.Channels switch { 1 => PdfName.Get("DeviceGray"), 3 => PdfName.Get("DeviceRGB"), 4 => PdfName.Get("DeviceCMYK"), _ => (PdfObject)PdfNull.Instance };
            if (space is PdfNull) return null;
            own = (space, jpx.Channels);
        }
        var alpha = (dict.GetInteger(PdfName.Get("SMaskInData")) ?? 0) != 0 && dict.Get(PdfName.Get("SMask")) is not PdfStream ? jpx.Alpha : null;
        return (jpx, own, alpha);
    }

    private static PdfDictionary Dict(params (string Key, PdfObject Value)[] entries)
    {
        var d = new PdfDictionary();
        foreach (var (key, value) in entries) d.Set(PdfName.Get(key), value);
        return d;
    }

    /// <summary>
    /// A JPEG through the own decoder at full size, in the channels of the colour space (<paramref name="n"/>; 0: any number, for an image
    /// without a colour space): the samples as decoded, after the transform of the Adobe marker or /ColorTransform, before /Decode — what the
    /// new image keeps with the same /Decode. Null if the decoder refuses it (unsupported process, beyond the budget) or its channels do not
    /// fit the colour space.
    /// </summary>
    internal static JpegImage? Jpeg(PdfStream stream, StreamFilters.DecodeResult decoded, int n)
    {
        try
        {
            var image = JpegDecoder.Decode(decoded.Data, new JpegOptions((int?)decoded.ImageParms?.GetInteger(PdfName.Get("ColorTransform")),
                stream.Dictionary.GetInteger(PdfName.Get("Height")) ?? 0, Budget: PdfLimits.Budget(4L << 30)));
            return n <= 0 && image.Channels is 1 or 3 or 4 || image.Channels == n ? image : null;
        }
        catch (JpegException)
        {
            return null;
        }
    }

    /// <summary>
    /// Raw value per component under the areas: for an image the darkest value of its colour space (after /Decode), for a stencil the
    /// value that paints nothing, for masks the one that lets the image show.
    /// </summary>
    private static int[] BlankValue(Role role, ColorSpace? space, int n, int bpc, double[]? decode)
    {
        var max = (1 << bpc) - 1;
        if (role != Role.Color || space is null)
        {
            double d0 = 0, d1 = 1;
            if (decode is { Length: >= 2 }) (d0, d1) = (decode[0], decode[1]);
            // stencil and explicit mask: value 1 (after /Decode) paints nothing, 0 paints; soft mask: the larger value is the more opaque
            var high = d1 >= d0 ? max : 0;
            var low = max - high;
            return Enumerable.Repeat(role == Role.ExplicitMask ? low : high, n).ToArray();
        }
        var ranges = decode is { } d && d.Length >= 2 * n ? d : space.DefaultDecode(bpc);
        // a damaged colour space can name fewer ranges than it has components (found by fuzzing): 0 … 1 for the others
        if (ranges.Length < 2 * n) ranges = [.. ranges, .. Enumerable.Range(0, 2 * n - ranges.Length).Select(i => (double)((ranges.Length + i) % 2))];
        IEnumerable<int[]> candidates = n == 1 && max <= 255 ? Enumerable.Range(0, max + 1).Select(v => new[] { v })
            : n <= 4 ? Enumerable.Range(0, 1 << n).Select(bits => Enumerable.Range(0, n).Select(c => (bits >> c & 1) * max).ToArray())
            : [new int[n], Enumerable.Repeat(max, n).ToArray()];
        int[] best = new int[n];
        var darkest = double.MaxValue;
        var components = new double[n];
        foreach (var candidate in candidates)
        {
            for (var c = 0; c < n; c++) components[c] = ranges[2 * c] + candidate[c] * (ranges[2 * c + 1] - ranges[2 * c]) / max;
            double luma;
            try
            {
                var rgb = space.ToRgb(components);
                luma = 0.299 * rgb.R + 0.587 * rgb.G + 0.114 * rgb.B;
            }
            catch (Exception e) when (e is PdfException or ArgumentException or IndexOutOfRangeException)
            {
                continue;
            }
            if (luma < darkest)
            {
                (darkest, best) = (luma, candidate);
                if (luma <= 0) break;
            }
        }
        return best;
    }

    /// <summary>Writes the refined rows with the blacked-out samples into <paramref name="output"/>, row by row.</summary>
    /// <param name="missing">The byte for rows the data lacks, as the display reads them: for a 1-bit mask set bits (nothing painted) — zeros
    /// painted a black line across a barcode of stencil masks whose data ran short (docs/FEHLERGEDAECHTNIS.md, No. 259).</param>
    private static void Write(Source source, Stream output, int width, int height, int n, int bpc, int kx, int ky, bool smooth,
        List<(double X, double Y)[]> quads, int[] blank, byte missing = 0)
    {
        var inBytes = (int)(((long)width * n * bpc + 7) / 8);
        var (w, h) = (width * kx, height * ky);
        var outBytes = (int)(((long)w * n * bpc + 7) / 8);
        var input = new byte[inBytes];
        var expanded = kx > 1 || smooth ? new byte[outBytes] : input;
        var row = new byte[outBytes];
        var spans = new List<(int, int)>();
        void Collect(int from, int to) => spans.Add((from, to));

        // bilinear: two unpacked source rows at a time
        int[]? a = null, b = null;
        int[]? x0 = null, x1 = null;
        double[]? fx = null;
        var loaded = -1;
        if (smooth)
        {
            (a, b) = (new int[width * n], new int[width * n]);
            (x0, x1, fx) = Weights(width, kx);
        }
        void Load(int index)
        {
            while (loaded < index)
            {
                if (!source.Rows.Read(input)) Array.Fill(input, missing);
                (a, b) = (b, a);
                for (var i = 0; i < width * n; i++) b![i] = Sample(input, i, bpc);
                loaded++;
                if (loaded == 0) Array.Copy(b!, a!, b!.Length);
            }
        }

        for (var y = 0; y < h; y++)
        {
            if (smooth)
            {
                var (r0, r1, fy) = Weight(y, height, ky);
                Load(r1);
                // after Load(r1), b holds row r1 and a row r1 − 1 (or the same row at the top edge)
                var top = r0 == r1 ? b! : a!;
                for (var x = 0; x < w; x++)
                    for (var c = 0; c < n; c++)
                    {
                        var v = Mix(top[x0![x] * n + c], top[x1![x] * n + c], b![x0[x] * n + c], b[x1[x] * n + c], fx![x], fy);
                        Put(expanded, (long)x * n + c, bpc, (int)Math.Round(v));
                    }
            }
            else if (y % ky == 0)
            {
                if (!source.Rows.Read(input)) Array.Fill(input, missing);
                if (kx > 1)
                {
                    Array.Clear(expanded);
                    for (var x = 0; x < w; x++)
                        for (var c = 0; c < n; c++)
                            Put(expanded, (long)x * n + c, bpc, Sample(input, (long)(x / kx) * n + c, bpc));
                }
            }
            spans.Clear();
            Spans(quads, y, w, Collect);
            if (spans.Count == 0)
            {
                output.Write(expanded, 0, outBytes);
                continue;
            }
            Array.Copy(expanded, row, outBytes);
            foreach (var (from, to) in spans) Fill(row, from, to, n, bpc, blank);
            output.Write(row, 0, outBytes);
        }
    }

    /// <summary>
    /// Sets the pixels [<paramref name="from"/>, <paramref name="to"/>) of a row to <paramref name="blank"/>: whole bytes at once where
    /// every sample is the same (a stencil or mask of 151 megapixels under a wide bar took seconds sample by sample), the bytes at the
    /// ends and mixed components one by one.
    /// </summary>
    private static void Fill(Span<byte> row, int from, int to, int n, int bpc, int[] blank)
    {
        long first = (long)from * n, last = (long)to * n;   // samples [first, last)
        var uniform = Array.TrueForAll(blank, v => v == blank[0]);
        if (!uniform || bpc == 16)
        {
            if (bpc == 8)
            {
                for (var x = from; x < to; x++)
                    for (var c = 0; c < n; c++) row[x * n + c] = (byte)blank[c];
                return;
            }
            for (var i = first; i < last; i++) Put(row, i, bpc, blank[(int)(i % n)]);
            return;
        }
        var value = blank[0];
        if (bpc == 8)
        {
            row.Slice((int)first, (int)(last - first)).Fill((byte)value);
            return;
        }
        // 1, 2 or 4 bits: the samples up to the next whole byte, the whole bytes, the rest
        var perByte = 8 / bpc;
        var i0 = first;
        for (; i0 < last && i0 % perByte != 0; i0++) Put(row, i0, bpc, value);
        var whole = (last - i0) / perByte;
        if (whole > 0)
        {
            var pattern = 0;
            for (var k = 0; k < perByte; k++) pattern = pattern << bpc | value;
            row.Slice((int)(i0 / perByte), (int)whole).Fill((byte)pattern);
            i0 += whole * perByte;
        }
        for (; i0 < last; i0++) Put(row, i0, bpc, value);
    }

    private static int Sample(ReadOnlySpan<byte> row, long index, int bpc)
    {
        switch (bpc)
        {
            case 8:
                return row[(int)index];
            case 16:
                return row[(int)(2 * index)] << 8 | row[(int)(2 * index + 1)];
        }
        var bit = index * bpc;
        return row[(int)(bit >> 3)] >> (8 - bpc - (int)(bit & 7)) & ((1 << bpc) - 1);
    }

    private static void Put(Span<byte> row, long index, int bpc, int value)
    {
        switch (bpc)
        {
            case 8:
                row[(int)index] = (byte)value;
                return;
            case 16:
                row[(int)(2 * index)] = (byte)(value >> 8);
                row[(int)(2 * index + 1)] = (byte)value;
                return;
        }
        var bit = index * bpc;
        var shift = 8 - bpc - (int)(bit & 7);
        var mask = ((1 << bpc) - 1) << shift;
        ref var target = ref row[(int)(bit >> 3)];
        target = (byte)(target & ~mask | value << shift & mask);
    }
}
