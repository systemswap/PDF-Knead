using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Cuts an image XObject in its own samples: the rows and columns kept, bit for bit, with its colour space, bit depth, /Decode, /Interpolate and
/// masks — the part kept looks exactly as before (a scan of 1 bit stays 1 bit, written as G4; nothing goes through RGB). Only for images whose
/// samples the filters give (not JPEG, JPEG 2000); otherwise the caller decodes and writes the pixels.
/// </summary>
internal static class ImageCut
{
    private static readonly PdfName Width = PdfName.Get("Width"), Height = PdfName.Get("Height"), Bpc = PdfName.Get("BitsPerComponent"),
        ImageMask = PdfName.Get("ImageMask"), ColorSpace = PdfName.Get("ColorSpace"), SMask = PdfName.Get("SMask"), Mask = PdfName.Get("Mask"),
        Dl = PdfName.Get("DL"), Alternates = PdfName.Get("Alternates"), Opi = PdfName.Get("OPI"), SMaskInData = PdfName.Get("SMaskInData");

    /// <summary>Largest image (bytes of samples) cut this way; larger ones go the decoded way with its limits.</summary>
    private const long MaxBytes = 256L << 20;

    /// <summary>
    /// The part of the unit square that holds <paramref name="left"/> … <paramref name="right"/>, <paramref name="top"/> … <paramref name="bottom"/>
    /// (fractions; row 0 at the top) and whose edges lie on whole samples of the image and of each of its masks (/SMask, a /Mask image), which
    /// may have other sizes. Cut to it, image and masks cover exactly the same part: cut each to the requested part alone, the image was widened
    /// to its samples while a finer mask was not, and the mask was stretched over the larger part — the shape it holds slid out of place
    /// (docs/FEHLERGEDAECHTNIS.md, No. 282).
    /// </summary>
    /// <param name="image">The image XObject.</param>
    /// <param name="left">Left edge (fraction of the width).</param>
    /// <param name="top">Top edge (fraction of the height, from the top).</param>
    /// <param name="right">Right edge.</param>
    /// <param name="bottom">Bottom edge.</param>
    /// <returns>The part; the whole square if no common edges are found within a few widenings.</returns>
    public static (double Left, double Top, double Right, double Bottom) Region(PdfStream image, double left, double top, double right, double bottom)
    {
        var sizes = new List<(int W, int H)>(3);
        void Add(PdfDictionary d)
        {
            if (d.GetInteger(Width) is > 0 and <= int.MaxValue and var w && d.GetInteger(Height) is > 0 and <= int.MaxValue and var h) sizes.Add(((int)w, (int)h));
        }
        Add(image.Dictionary);
        foreach (var maskKey in (ReadOnlySpan<PdfName>)[SMask, Mask])
            if (image.Dictionary.Get(maskKey) is PdfStream mask) Add(mask.Dictionary);
        // each widening moves an edge outwards onto a sample of one of the sizes; once no edge moves, all of them agree
        for (var round = 0; round < 16; round++)
        {
            var (l, t, r, b) = (left, top, right, bottom);
            foreach (var (w, h) in sizes)
            {
                var (x0, y0, x1, y1) = Range(w, h, l, t, r, b);
                (l, t, r, b) = (Math.Min(l, (double)x0 / w), Math.Min(t, (double)y0 / h), Math.Max(r, (double)x1 / w), Math.Max(b, (double)y1 / h));
            }
            if (Same(l, left) && Same(t, top) && Same(r, right) && Same(b, bottom)) return (left, top, right, bottom);
            (left, top, right, bottom) = (l, t, r, b);
        }
        return (0, 0, 1, 1);

        static bool Same(double a, double b) => Math.Abs(a - b) < 1e-12;
    }

    /// <summary>
    /// The image cut to the part <paramref name="left"/> … <paramref name="right"/>, <paramref name="top"/> … <paramref name="bottom"/> of its unit
    /// square (fractions; row 0 at the top), widened to whole samples; its masks (/SMask, a /Mask image) cut to the same part in their own size.
    /// Give the part from <see cref="Region"/>: only then do image and masks cover the same part.
    /// Other versions of it (/Alternates, /OPI) are left out: they hold the whole image.
    /// </summary>
    /// <param name="doc">The document of the image.</param>
    /// <param name="image">The image XObject.</param>
    /// <param name="resources">The resources it is drawn with (for a colour space given by name).</param>
    /// <returns>The cut image; null if its samples are not available this way (JPEG, JPEG 2000, broken or unusual data).</returns>
    public static PdfStream? Cut(PdfDocument doc, PdfStream image, PdfDictionary? resources, double left, double top, double right, double bottom) =>
        Cut(doc, image, resources, left, top, right, bottom, isMask: false);

    /// <param name="isMask">The image is a mask of another: its own masks are left out (a mask has none, ISO 32000-2, 11.6.5.3) — followed,
    /// an image whose /SMask was itself cut its mask without end, and the stack ran out (docs/FEHLERGEDAECHTNIS.md, No. 380).</param>
    private static PdfStream? Cut(PdfDocument doc, PdfStream image, PdfDictionary? resources, double left, double top, double right, double bottom, bool isMask)
    {
        var dict = image.Dictionary;
        int w = (int)Math.Clamp(dict.GetInteger(Width) ?? 0, 0, int.MaxValue), h = (int)Math.Clamp(dict.GetInteger(Height) ?? 0, 0, int.MaxValue);
        if (w <= 0 || h <= 0 || dict.Get(SMaskInData) is PdfInteger { Value: not 0 }) return null;
        var stencil = dict.Get(ImageMask) is PdfBoolean { Value: true };
        var bpc = stencil ? 1 : (int)(dict.GetInteger(Bpc) ?? 0);
        if (bpc is not (1 or 2 or 4 or 8 or 16)) return null;
        int components;
        try
        {
            components = stencil ? 1 : dict.Get(ColorSpace) is { } space ? Graphics.ColorSpace.Create(space, resources, doc).Components : 0;
        }
        catch (PdfException)
        {
            return null;
        }
        if (components is < 1 or > 32) return null;
        var bitsPerPixel = components * bpc;
        var rowBytes = ((long)w * bitsPerPixel + 7) / 8;
        if (rowBytes * h > MaxBytes) return null;

        // the samples: through the filters (JBIG2 decoded to rows as well); JPEG and JPEG 2000 are not samples
        StreamFilters.DecodeResult decoded;
        try
        {
            decoded = doc.DecodeStream(image, (int)(rowBytes * h));
            if (decoded.ImageFilter?.Value == "JBIG2Decode" && bpc == 1 && components == 1) decoded = PdfImageDecoder.Jbig2(decoded, doc, w, h);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException)
        {
            return null;
        }
        if (decoded.ImageFilter is not null || decoded.Truncated || decoded.Data.Length < rowBytes * h) return null;

        var (x0, y0, x1, y1) = Range(w, h, left, top, right, bottom);
        int cw = x1 - x0, ch = y1 - y0;
        var cutRow = ((long)cw * bitsPerPixel + 7) / 8;
        var samples = new byte[cutRow * ch];
        for (var y = 0; y < ch; y++)
            CopyBits(decoded.Data.AsSpan((int)((y0 + y) * rowBytes), (int)rowBytes), (long)x0 * bitsPerPixel, samples.AsSpan((int)(y * cutRow), (int)cutRow),
                (long)cw * bitsPerPixel);

        var result = new PdfDictionary(dict.Count);
        foreach (var (key, value) in dict.Entries)
            if (key != PdfName.Filter && key != PdfName.DecodeParms && key != PdfName.DP && key != PdfName.Length && key != Dl && key != Alternates && key != Opi)
                result.Set(key, value);
        result.Set(Width, PdfInteger.Of(cw));
        result.Set(Height, PdfInteger.Of(ch));
        foreach (var maskKey in (ReadOnlySpan<PdfName>)[SMask, Mask])
            if (isMask) result.Remove(maskKey);
            else if (dict.Get(maskKey) is PdfStream mask)
            {
                if (Cut(doc, mask, resources, left, top, right, bottom, isMask: true) is not { } cutMask) return null;
                result.Set(maskKey, cutMask);
            }
        // one bit of one component (scans, stencil masks): G4 as for the compression, decoding to the same samples
        if (bitsPerPixel == 1)
        {
            var parms = new PdfDictionary();
            parms.Set(PdfName.Get("K"), PdfInteger.Of(-1));
            parms.Set(PdfName.Get("Columns"), PdfInteger.Of(cw));
            parms.Set(PdfName.Get("Rows"), PdfInteger.Of(ch));
            parms.Set(PdfName.Get("BlackIs1"), PdfBoolean.False);
            result.Set(PdfName.Filter, PdfName.Get("CCITTFaxDecode"));
            result.Set(PdfName.DecodeParms, parms);
            return new PdfStream(result, CcittEncoder.EncodeG4(samples, cw, ch, (int)cutRow, blackIs1: false));
        }
        return new PdfStream(result, samples);   // compressed when written
    }

    /// <summary>
    /// A JPEG or JPEG 2000 image with a mask (/SMask, a /Mask image), cut to a part from <see cref="Region"/>: the colour in its own size and
    /// colour space — a grey or colour JPEG written as a JPEG again at <paramref name="jpegQuality"/>, a CMYK JPEG and JPEG 2000 without loss in
    /// their 8-bit channels (the JPEG read by the own decoder in its own channels, as display and redaction read it; docs/FEHLERGEDAECHTNIS.md,
    /// No. 805) —, and the masks cut in their own samples to the same part (<see cref="Cut"/>). Before, such an image was decoded with its mask
    /// into RGBA at the finer size and written without loss: far larger, its own colour space gone.
    /// </summary>
    /// <param name="doc">The document of the image.</param>
    /// <param name="image">The image XObject.</param>
    /// <param name="resources">The resources it is drawn with.</param>
    /// <param name="left">Left edge of the part (from <see cref="Region"/>).</param>
    /// <param name="top">Top edge.</param>
    /// <param name="right">Right edge.</param>
    /// <param name="bottom">Bottom edge.</param>
    /// <param name="codec">Codec that writes the new JPEG.</param>
    /// <param name="jpegQuality">Quality for the new JPEG.</param>
    /// <returns>The cut image; null if it has no mask, is no JPEG or JPEG 2000, or cannot be read this way (the caller then goes the general way).</returns>
    public static PdfStream? CutEncoded(PdfDocument doc, PdfStream image, PdfDictionary? resources, double left, double top, double right, double bottom,
        Graphics.IImageCodec? codec, int jpegQuality)
    {
        var dict = image.Dictionary;
        if (dict.Get(SMask) is not PdfStream && dict.Get(Mask) is not PdfStream) return null;
        int w = (int)Math.Clamp(dict.GetInteger(Width) ?? 0, 0, int.MaxValue), h = (int)Math.Clamp(dict.GetInteger(Height) ?? 0, 0, int.MaxValue);
        StreamFilters.DecodeResult decoded;
        try
        {
            decoded = doc.DecodeStream(image);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException)
        {
            return null;
        }
        var spaceObj = dict.Get(ColorSpace);
        int Components(PdfObject space)
        {
            try
            {
                return space is PdfNull ? 0 : Graphics.ColorSpace.Create(space, resources, doc).Components;
            }
            catch (PdfException)
            {
                return 0;
            }
        }
        var own = Components(spaceObj);
        int n;
        byte[] samples;
        PdfObject space;
        bool jpeg;
        // the samples are the raw values in the image's own colour space: its /Decode keeps counting (docs/FEHLERGEDAECHTNIS.md, No. 739); JPEG
        // 2000 in the colour space of its data already ignored it
        bool ownSamples;
        switch (decoded.ImageFilter?.Value)
        {
            // at full size only within the budget of the machine (No. 743; the decoder checks it before it takes memory); otherwise the caller
            // goes the general way. A JPEG without a colour space (not allowed for an image, 8.9.5.1) by the number of its channels
            case "DCTDecode" when codec is not null && ImageBlanker.Jpeg(image, decoded, own) is { } j && (j.Width, j.Height) == (w, h):
                (n, samples, ownSamples) = (j.Channels, j.Samples, own > 0);
                space = own > 0 ? spaceObj : PdfName.Get(n switch { 1 => "DeviceGray", 3 => "DeviceRGB", _ => "DeviceCMYK" });
                // the codec writes grey and colour; four channels (CMYK) go without loss
                jpeg = n is 1 or 3;
                break;
            case "JPXDecode" when ImageBlanker.DecodeJpx(image, decoded.Data, own) is { } jpx && (jpx.Image.Width, jpx.Image.Height) == (w, h) && jpx.Alpha is null:
                (jpeg, n, samples, space) = (false, jpx.Image.Channels, jpx.Image.Samples, jpx.OwnSpace?.Space ?? spaceObj);
                ownSamples = jpx.OwnSpace is null && !(spaceObj is PdfArray { Count: > 0 } indexed && indexed.Get(0) is PdfName { Value: "Indexed" or "I" });
                break;
            default:
                return null;
        }
        var (x0, y0, x1, y1) = Range(w, h, left, top, right, bottom);
        int cw = x1 - x0, ch = y1 - y0;
        var cut = new byte[(long)cw * ch * n];
        for (var y = 0; y < ch; y++)
            Array.Copy(samples, ((long)(y0 + y) * w + x0) * n, cut, (long)y * cw * n, (long)cw * n);
        var result = new PdfDictionary(dict.Count);
        foreach (var (key, value) in dict.Entries)
            if (key.Value is not ("Filter" or "DecodeParms" or "DP" or "Length" or "DL" or "Alternates" or "OPI" or "Width" or "Height" or "ColorSpace" or
                "BitsPerComponent" or "Decode" or "SMaskInData" or "SMask" or "Mask" or "Metadata"))
                result.Set(key, value);
        result.Set(Width, PdfInteger.Of(cw));
        result.Set(Height, PdfInteger.Of(ch));
        result.Set(ColorSpace, space);
        result.Set(Bpc, PdfInteger.Of(8));
        if (ownSamples && dict.Get(PdfName.Get("Decode")) is PdfArray decodeArray) result.Set(PdfName.Get("Decode"), decodeArray);
        foreach (var maskKey in (ReadOnlySpan<PdfName>)[SMask, Mask])
            if (dict.Get(maskKey) is PdfStream mask)
            {
                if (Cut(doc, mask, resources, left, top, right, bottom, isMask: true) is not { } cutMask) return null;
                result.Set(maskKey, cutMask);
            }
        if (!jpeg) return new PdfStream(result, cut);   // compressed when written
        if (codec!.EncodeJpeg(cw, ch, n, cut, Math.Clamp(jpegQuality, 50, 100)) is not { } data) return null;
        result.Set(PdfName.Filter, PdfName.Get("DCTDecode"));
        return new PdfStream(result, data);
    }

    /// <summary>The samples of a <paramref name="w"/> × <paramref name="h"/> image that reach into a part of its unit square (end exclusive).</summary>
    /// <summary>
    /// What <see cref="Cut"/> takes of an image and its masks for a part: their sample ranges, as text. Two parts with the same key give the same
    /// cut image (the fractions may differ in their last digits).
    /// </summary>
    internal static string PartKey(PdfStream image, double left, double top, double right, double bottom)
    {
        var key = new System.Text.StringBuilder();
        foreach (var dict in (PdfDictionary?[])[image.Dictionary, (image.Dictionary.Get(SMask) as PdfStream)?.Dictionary, (image.Dictionary.Get(Mask) as PdfStream)?.Dictionary])
            if (dict?.GetInteger(Width) is > 0 and <= int.MaxValue and var w && dict.GetInteger(Height) is > 0 and <= int.MaxValue and var h)
                key.Append(Range((int)w, (int)h, left, top, right, bottom)).Append(';');
        return key.ToString();
    }

    internal static (int X0, int Y0, int X1, int Y1) Range(int w, int h, double left, double top, double right, double bottom)
    {
        var x0 = Math.Clamp((int)Math.Floor(left * w + 1e-6), 0, w - 1);
        var x1 = Math.Clamp((int)Math.Ceiling(right * w - 1e-6), x0 + 1, w);
        var y0 = Math.Clamp((int)Math.Floor(top * h + 1e-6), 0, h - 1);
        var y1 = Math.Clamp((int)Math.Ceiling(bottom * h - 1e-6), y0 + 1, h);
        return (x0, y0, x1, y1);
    }

    /// <summary>Copies <paramref name="count"/> bits from bit <paramref name="from"/> of <paramref name="source"/> to the start of <paramref name="target"/> (most significant bit first).</summary>
    private static void CopyBits(ReadOnlySpan<byte> source, long from, Span<byte> target, long count)
    {
        if ((from & 7) == 0)
        {
            var bytes = (int)(count >> 3);
            source.Slice((int)(from >> 3), bytes).CopyTo(target);
            if ((count & 7) != 0)
            {
                var keep = (byte)(0xFF << (8 - (int)(count & 7)));
                target[bytes] = (byte)(source[(int)(from >> 3) + bytes] & keep);
            }
            return;
        }
        target[..(int)((count + 7) >> 3)].Clear();
        var shift = (int)(from & 7);
        var start = (int)(from >> 3);
        for (var i = 0; i < (count + 7) >> 3; i++)
        {
            var hi = source[start + i] << shift;
            var lo = start + i + 1 < source.Length ? source[start + i + 1] >> (8 - shift) : 0;
            var b = (byte)(hi | lo);
            var bitsHere = Math.Min(8, count - i * 8L);
            if (bitsHere < 8) b &= (byte)(0xFF << (8 - (int)bitsHere));
            target[i] = b;
        }
    }
}
