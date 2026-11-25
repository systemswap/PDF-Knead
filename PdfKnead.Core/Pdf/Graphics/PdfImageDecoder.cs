using System.Numerics;
using System.Runtime.InteropServices;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Filters.Jpeg;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Graphics;

/// <summary>Pixel layout of a decoded image.</summary>
public enum ImagePixels : byte
{
    /// <summary>4 bytes per pixel, RGBA, not premultiplied.</summary>
    Rgba,

    /// <summary>1 byte per pixel, grey value, opaque (greyscale and 1-bit images without a mask — a quarter of RGBA).</summary>
    Gray,

    /// <summary>1 byte per pixel, coverage; painted in <see cref="DecodedImage.Color"/> (stencil masks, /ImageMask).</summary>
    Alpha,
}

/// <summary>Decoded image, row by row from the top; layout see <see cref="Format"/>.</summary>
/// <param name="Width">Width in pixels (may be smaller than in the PDF if the image was reduced).</param>
/// <param name="Height">Height in pixels (may be smaller than in the PDF if the image was reduced).</param>
/// <param name="Pixels">The pixels, <see cref="BytesPerPixel"/> bytes each, without row padding.</param>
/// <param name="Format">The pixel layout.</param>
/// <param name="Color">For <see cref="ImagePixels.Alpha"/>: colour of the stencil mask (ARGB).</param>
public sealed record DecodedImage(int Width, int Height, byte[] Pixels, ImagePixels Format = ImagePixels.Rgba, uint Color = 0)
{
    /// <summary>4 for <see cref="ImagePixels.Rgba"/>, otherwise 1.</summary>
    public int BytesPerPixel => Format == ImagePixels.Rgba ? 4 : 1;

    /// <summary>The pixels as RGBA (converted for grey and coverage, allocating a new array each time — only for rare paths and tests).</summary>
    public byte[] Rgba
    {
        get
        {
            if (Format == ImagePixels.Rgba) return Pixels;
            var rgba = new byte[Pixels.Length * 4];
            byte r = (byte)(Color >> 16), g = (byte)(Color >> 8), b = (byte)Color, a = (byte)(Color >> 24);
            for (var i = 0; i < Pixels.Length; i++)
            {
                var v = Pixels[i];
                if (Format == ImagePixels.Gray)
                {
                    rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = v;
                    rgba[i * 4 + 3] = 255;
                }
                else
                {
                    rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b;
                    rgba[i * 4 + 3] = (byte)(v * a / 255);
                }
            }
            return rgba;
        }
    }
}

/// <summary>
/// Writes JPEG for the writer (compression, cutting) through the renderer's codec. Decoding is the core's own: every image format, JPEG
/// through <see cref="Filters.Jpeg.JpegDecoder"/> (docs/FEHLERGEDAECHTNIS.md, No. 805).
/// </summary>
public interface IImageCodec
{
    /// <summary>Encodes as JPEG (compression): 1 component grey, 3 RGB or 4 RGBA (alpha is ignored), 8 bits.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="components">1, 3 or 4.</param>
    /// <param name="samples">The samples, row by row without padding.</param>
    /// <param name="quality">JPEG quality.</param>
    /// <returns>The JPEG data; null if the codec cannot encode (the default implementation).</returns>
    byte[]? EncodeJpeg(int width, int height, int components, ReadOnlySpan<byte> samples, int quality) => null;
}

/// <summary>
/// Images according to ISO 32000-2, 8.9: 1–16 bits per component, all colour spaces, /Decode,
/// stencil masks (/ImageMask), soft masks (/SMask), colour key and image masks (/Mask).
/// Large images (scans, plans) are read row by row and reduced to at most
/// <see cref="MaxFullPixels"/> on the way, so that memory stays within bounds even for 500-megapixel
/// scans.
/// </summary>
public static class PdfImageDecoder
{
    /// <summary>The colour the display paints in place of an image it cannot decode (RGB).</summary>
    public static readonly (byte R, byte G, byte B) Placeholder = (0xE6, 0xE8, 0xEC);

    /// <summary>Images larger than this are not touched (protection against nonsensical values): 4 gigapixels.</summary>
    public const long MaxPixels = 4_000_000_000;

    /// <summary>Up to this many pixels at full size, above that reduced (16 MP = 64 MB RGBA).</summary>
    public const long MaxFullPixels = 16_000_000;

    /// <summary>At most this many pixels for width and height each (otherwise row buffers overflow; real scans: A0 at 1200 dpi ≈ 56,000).</summary>
    public const int MaxSide = 1 << 20;

    /// <summary>
    /// Image with a mask: at most this many pixels (more only if the image itself is larger). Measured on scanned plans with masks
    /// up to 20,595 × 28,611 and presentation-slide text (2 × 2 colour, huge mask): 2 million squeezed plans to about 1,500 pixels wide and thin
    /// lines faded; 4 million recovers most of the sharpness at no extra cost; 16 million would be slightly sharper but costs about 27 %
    /// more time and twice the memory.
    /// </summary>
    private const long MaxMaskedPixels = 4_000_000;

    /// <summary>How large an image is decoded before it is reduced: the image alone, and the image with its mask.</summary>
    private readonly record struct Limits(long Full, long Masked);

    /// <summary>For the display: <see cref="MaxFullPixels"/>, with a mask <see cref="MaxMaskedPixels"/> (more only if the image itself is larger).</summary>
    private static readonly Limits Display = new(MaxFullPixels, MaxMaskedPixels);

    /// <summary>Decodes an image of the display list.</summary>
    /// <param name="image">The image source.</param>
    /// <param name="doc">The document (stream data, colour spaces).</param>
    /// <returns>The image (reduced if large); null if it cannot be read or exceeds the limits.</returns>
    public static DecodedImage? Decode(ImageSource image, PdfDocument doc)
    {
        using var page = ColorSpace.ForPage(image.DeviceCmyk);
        // the colour conversion of one image bounded on its own, the same whatever was drawn before (No. 908)
        using var work = FunctionWork.Begin(doc);
        try
        {
            return DecodeCore(image.Dictionary, image.Stream, image.Inline?.Data, image.Resources, doc, image.IsStencil, image.StencilColor, allowMask: true,
                Display, image.Intent, image.Bpc);
        }
        catch (PdfLimitException)
        {
            return null;
        }
        catch (PdfException)
        {
            return null;
        }
    }

    /// <summary>
    /// The opacity a mask (/SMask, or /Mask as a stream) gives, at the mask's own size, as the display reads it (with /Decode; an image mask
    /// visible where a stencil mask would paint).
    /// </summary>
    /// <param name="mask">The mask.</param>
    /// <param name="doc">The document.</param>
    /// <returns>Width, height and one byte per pixel (255 opaque); null if it cannot be read or exceeds the limits.</returns>
    public static (int Width, int Height, byte[] Alpha)? MaskOpacity(PdfStream mask, PdfDocument doc)
    {
        var w = (int)(Get(mask.Dictionary, "Width", "W").AsNumber() ?? 0);
        var h = (int)(Get(mask.Dictionary, "Height", "H").AsNumber() ?? 0);
        if (w <= 0 || h <= 0 || w > MaxSide || h > MaxSide || (long)w * h > MaxFullPixels) return null;
        try
        {
            return MaskAlpha(mask, doc, w, h, new Limits(MaxFullPixels, MaxMaskedPixels)) is { } alpha && alpha.Length == w * h ? (w, h, alpha) : null;
        }
        catch (PdfException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes an image as the display does, but reduced from <paramref name="maxPixels"/> on (also with a mask): for checks that compare two
    /// readings of an image, where a large scan need not be read in full.
    /// </summary>
    /// <param name="image">The image source.</param>
    /// <param name="doc">The document.</param>
    /// <param name="maxPixels">Largest number of pixels kept; larger images are reduced (not above the display's own limits).</param>
    /// <returns>The image; null if it cannot be read or exceeds the limits.</returns>
    public static DecodedImage? DecodeReduced(ImageSource image, PdfDocument doc, long maxPixels)
    {
        using var page = ColorSpace.ForPage(image.DeviceCmyk);
        using var work = FunctionWork.Begin(doc);   // No. 908
        try
        {
            var limits = new Limits(Math.Min(maxPixels, MaxFullPixels), Math.Min(maxPixels, MaxMaskedPixels));
            return DecodeCore(image.Dictionary, image.Stream, image.Inline?.Data, image.Resources, doc, image.IsStencil, image.StencilColor, allowMask: true,
                limits, image.Intent, image.Bpc);
        }
        catch (PdfLimitException)
        {
            return null;
        }
        catch (PdfException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes an image at its full size — not reduced as for the display — up to <paramref name="maxPixels"/> for the image and for the image
    /// with its mask (cutting an image keeps its resolution). Callers check the size beforehand (<see cref="FullSize"/>).
    /// </summary>
    /// <param name="image">The image source.</param>
    /// <param name="doc">The document.</param>
    /// <param name="maxPixels">Largest number of pixels decoded without reducing.</param>
    /// <returns>The image; null if it cannot be read or exceeds the limits.</returns>
    public static DecodedImage? DecodeFull(ImageSource image, PdfDocument doc, long maxPixels)
    {
        using var page = ColorSpace.ForPage(image.DeviceCmyk);
        using var work = FunctionWork.Begin(doc);   // No. 908
        try
        {
            var limits = new Limits(Math.Max(maxPixels, MaxFullPixels), Math.Max(maxPixels, MaxMaskedPixels));
            return DecodeCore(image.Dictionary, image.Stream, image.Inline?.Data, image.Resources, doc, image.IsStencil, image.StencilColor, allowMask: true,
                limits, image.Intent, image.Bpc);
        }
        catch (PdfLimitException)
        {
            return null;
        }
        catch (PdfException)
        {
            return null;
        }
    }

    /// <summary>A row of an image at its full size: its number from the top and its pixels — RGBA, or for a stencil mask the coverage
    /// (1 byte per pixel, 255 where it paints).</summary>
    internal delegate void FullRow(int y, ReadOnlySpan<byte> pixels);

    /// <summary>
    /// Reads an image at its full size row by row, its samples converted as the display converts them (the rows of its data, its colour
    /// space, /Decode), and hands each row from <paramref name="first"/> to <paramref name="last"/> to <paramref name="row"/>; no whole
    /// image is kept. For the check of a blacked-out copy under the areas at full size — reduced, a narrow rest under a bar of a large scan
    /// was averaged away (docs/FEHLERGEDAECHTNIS.md, No. 439).
    /// </summary>
    /// <param name="stream">The image (or the mask) stream.</param>
    /// <param name="resources">Resources for a named colour space.</param>
    /// <param name="doc">The document.</param>
    /// <param name="stencil">The stream is a stencil mask (/ImageMask, or an image's /Mask stream).</param>
    /// <param name="first">First row handed on.</param>
    /// <param name="last">Last row handed on (rows after it are not read).</param>
    /// <param name="row">Receives the rows.</param>
    /// <returns>The size of the image; null if it cannot be read row by row (a codec such as JPEG, a colour key, an unusual depth).</returns>
    internal static (int Width, int Height)? ReadRows(PdfStream stream, PdfDictionary? resources, PdfDocument doc, bool stencil, int first, int last,
        FullRow row)
    {
        IRowSource? rows = null;
        try
        {
            var dict = stream.Dictionary;
            var width = (int)(Get(dict, "Width", "W").AsNumber() ?? 0);
            var height = (int)(Get(dict, "Height", "H").AsNumber() ?? 0);
            if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || (long)width * height > MaxPixels) return null;
            var bpc = stencil ? 1 : (int)(Get(dict, "BitsPerComponent", "BPC").AsNumber() ?? 8);
            if (bpc is not (1 or 2 or 4 or 8 or 16) || !stencil && Get(dict, "Mask", "Mask") is PdfArray) return null;
            var decodeArray = PdfFunction.Numbers(Get(dict, "Decode", "D") as PdfArray);
            var spaceObj = Get(dict, "ColorSpace", "CS");
            var space = stencil || spaceObj is PdfNull ? ColorSpace.DeviceGray : ColorSpace.Create(spaceObj, resources, doc);
            var rowBytesLong = ((long)width * Math.Max(space.Components, 1) * bpc + 7) / 8;
            if (rowBytesLong > int.MaxValue / 2) return null;
            var rowBytes = (int)rowBytesLong;
            var inverted = decodeArray is { Length: >= 2 } && decodeArray[0] > decodeArray[1];
            rows = ImageRows.Open(doc, stream, rowBytes);
            if (rows is null)
            {
                var decoded = doc.DecodeStream(stream, (int)Math.Min(int.MaxValue / 2, rowBytesLong * height));
                if (decoded.ImageFilter is not null) return null;
                rows = new ImageRowsArray(decoded.Data, rowBytes);
            }
            var converter = stencil ? null : new RowConverter(space, bpc, decodeArray);
            var raw = new byte[rowBytes];
            var pixels = new byte[stencil ? width : width * 4];
            var ended = false;
            last = Math.Min(last, height - 1);
            for (var y = 0; y <= last; y++)
            {
                if (!ended && !rows.Read(raw)) ended = true;
                // rows after the end of the data: empty, as the display reads them (a stencil paints nothing there)
                if (ended)
                {
                    if (stencil) raw.AsSpan().Fill(inverted ? (byte)0 : (byte)0xFF);
                    else Array.Clear(raw);
                }
                if (y < first) continue;
                if (converter is null)
                    for (var x = 0; x < width; x++)
                    {
                        var bit = (raw[x >> 3] >> (7 - (x & 7))) & 1;
                        pixels[x] = (inverted ? bit == 1 : bit == 0) ? (byte)255 : (byte)0;
                    }
                else converter.Convert(raw, width, pixels, []);
                row(y, pixels);
            }
            return (width, height);
        }
        catch (PdfLimitException)
        {
            return null;
        }
        catch (PdfException)
        {
            return null;
        }
        finally
        {
            (rows as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Pixels of an image at its full size: the larger of the image and its mask (/SMask or /Mask as a stream) — an image with a finer mask
    /// is decoded at the mask's size.
    /// </summary>
    /// <param name="image">The image source.</param>
    /// <returns>The number of pixels; 0 if the size is unreadable.</returns>
    public static long FullSize(ImageSource image)
    {
        var dict = image.Dictionary;
        long Size(PdfDictionary d) => (long)Math.Max(0, Get(d, "Width", "W").AsNumber() ?? 0) * (long)Math.Max(0, Get(d, "Height", "H").AsNumber() ?? 0);
        var size = Size(dict);
        if (image.Stream is not null && (dict.Get(PdfName.Get("SMask")) as PdfStream ?? dict.Get(PdfName.Get("Mask")) as PdfStream) is { } mask)
        {
            var (w, h) = ((long)(Get(mask.Dictionary, "Width", "W").AsNumber() ?? 0), (long)(Get(mask.Dictionary, "Height", "H").AsNumber() ?? 0));
            var (iw, ih) = ((long)(Get(dict, "Width", "W").AsNumber() ?? 0), (long)(Get(dict, "Height", "H").AsNumber() ?? 0));
            size = Math.Max(size, Math.Max(iw, w) * Math.Max(ih, h));
        }
        return size;
    }

    // the full key, else its abbreviation (inline images); an entry whose reference leads nowhere counts as absent (7.3.9; S-A A67,
    // docs/FEHLERGEDAECHTNIS.md, No. 948)
    internal static PdfObject Get(PdfDictionary d, string full, string abbr) =>
        d.Get(PdfName.Get(full)) is not PdfNull and var value ? value : d.Get(PdfName.Get(abbr));

    private static DecodedImage? DecodeCore(PdfDictionary dict, PdfStream? stream, byte[]? inlineData, PdfDictionary? resources,
        PdfDocument doc, bool stencil, uint stencilColor, bool allowMask, Limits limits,
        byte renderingIntent = ColorSpace.RelativeColorimetric, byte blackPoint = ColorSpace.BpcDefault)
    {
        var width = (int)(Get(dict, "Width", "W").AsNumber() ?? 0);
        var height = (int)(Get(dict, "Height", "H").AsNumber() ?? 0);
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || (long)width * height > MaxPixels) return null;

        var bpc = stencil ? 1 : (int)(Get(dict, "BitsPerComponent", "BPC").AsNumber() ?? 8);
        var decodeArray = PdfFunction.Numbers(Get(dict, "Decode", "D") as PdfArray);
        var spaceObj = Get(dict, "ColorSpace", "CS");
        // the variant for the rendering intent and black point compensation (ISO 32000-2, 8.6.5.8, 8.6.5.9); for CMYK JPEGs it also gives
        // the profile handed to the codec (docs/FEHLERGEDAECHTNIS.md, No. 573)
        var space = stencil || spaceObj is PdfNull ? ColorSpace.DeviceGray : ColorSpace.Create(spaceObj, resources, doc).WithRendering(renderingIntent, blackPoint);
        var n = Math.Max(space.Components, 1);
        var rowBytesLong = ((long)width * n * Math.Clamp(bpc, 1, 32) + 7) / 8;
        if (rowBytesLong > int.MaxValue / 2) return null;
        var rowBytes = (int)rowBytesLong;
        // a colour key (/Mask as an array) — unless an /SMask overrides it (ISO 32000-2, 8.9.5.1, Table 87; docs/FEHLERGEDAECHTNIS.md, No. 764)
        var colorKey = stencil || dict.Get(PdfName.Get("SMask")) is PdfStream ? null : Get(dict, "Mask", "Mask") as PdfArray;
        var keyRanges = colorKey is null ? null : PdfFunction.Numbers(colorKey);
        var inverted = decodeArray is { Length: >= 2 } && decodeArray[0] > decodeArray[1];

        // soft mask (greyscale alpha) or image mask (1 bit)
        // masks have no mask of their own (standard); otherwise a mask that points to itself causes a stack overflow, which ends the program
        var mask = stream is null || !allowMask ? null
            : dict.Get(PdfName.Get("SMask")) as PdfStream ?? dict.Get(PdfName.Get("Mask")) as PdfStream;
        // without a mask and colour key, 1 byte per pixel suffices for grey images, with the same values as in RGBA
        var opaque = !stencil && colorKey is null && mask is null;

        byte[] rgba;
        // large: row by row and reduced (a colour key per raw pixel on the way)
        if ((long)width * height > limits.Full && stream is not null && bpc is 1 or 2 or 4 or 8 or 16
            && ImageRows.Open(doc, stream, rowBytes) is { } rows)
        {
            using (rows)
            {
                if (stencil)
                {
                    var (alpha, w, h) = ReducedStencil(rows, width, height, rowBytes, inverted, limits.Full);
                    return new DecodedImage(w, h, alpha, ImagePixels.Alpha, stencilColor);
                }
                var converter = new RowConverter(space, bpc, decodeArray);
                var gray = opaque && converter.IsGray;
                (rgba, width, height) = Reduced(rows, width, height, rowBytes, converter, gray, limits.Full, keyRanges);
                if (gray) return new DecodedImage(width, height, rgba, ImagePixels.Gray);
            }
        }
        else
        {
            // expected size of the raw data, so that Flate decodes into an array of exactly the right size
            var hint = (int)Math.Min(int.MaxValue / 2, (long)rowBytes * height);
            var decoded = stream is not null ? doc.DecodeStream(stream, hint) : StreamFilters.Decode(inlineData ?? [], InlineFilters(dict), hint);
            if (decoded.ImageFilter is { Value: "JBIG2Decode" }) decoded = Jbig2(decoded, doc, width, height);

            if (decoded.ImageFilter is { Value: "JPXDecode" })
            {
                // the core decodes JPEG 2000 itself (large images reduced right away)
                // a /ColorSpace of the image that fits the channels determines them, the colour specifications of the data are ignored
                // (ISO 32000-2, 7.4.9)
                ColorSpace? declared = spaceObj is not PdfNull && !stencil ? space : null;
                // /SMaskInData for data without a channel definition, channels no colour fits (docs/FEHLERGEDAECHTNIS.md, No. 996)
                if (Jpx(decoded.Data, doc, applyPalette: !(declared?.IsIndexed ?? false), n => declared is null || declared.Components != n, limits.Full,
                        declared is { IsIndexed: true } ? decodeArray : null, SMaskInData(dict), declared?.Components) is not { } decodedJpx) return null;
                var jpx = stencil ? decodedJpx : decodedJpx.Fitted(declared?.Components);
                (width, height) = (jpx.Width, jpx.Height);
                if (stencil) return JpxStencil(jpx, inverted, stencilColor);
                var jpxSpace = JpxSpace(declared, jpx, doc, renderingIntent, blackPoint);
                // /Decode counts with a /ColorSpace of the image (on the samples stretched to 8 bits, the same mapping), without one it is
                // ignored (ISO 32000-2, 7.4.9); for /Indexed the decoder has mapped the raw indices with their own bits already
                // (docs/FEHLERGEDAECHTNIS.md, No. 739, 945)
                // an image in the wide working space from its colours at 16 bits: its 8-bit steps are coarser than those of sRGB
                // (docs/FEHLERGEDAECHTNIS.md, No. 1092)
                var fine = jpx.Fine is { } f && jpxSpace.Components == jpx.Channels ? f : null;
                var converter = new RowConverter(jpxSpace, fine is null ? 8 : 16, ReferenceEquals(jpxSpace, declared) && !declared.IsIndexed ? decodeArray : null,
                    exact: fine is not null);
                var jpxData = fine ?? jpx.Samples;
                // opacity from the image only with /SMaskInData and without a separate /SMask (ISO 32000-2, 8.9.5.1)
                var alpha = mask is null && (dict.GetInteger(PdfName.Get("SMaskInData")) ?? 0) != 0 ? jpx.Alpha : null;
                if (opaque && alpha is null && converter.IsGray) return new DecodedImage(width, height, ConvertGray(jpxData, width, height, converter), ImagePixels.Gray);
                rgba = Convert(jpxData, width, height, converter, null);
                if (alpha is not null) ApplyAlpha(rgba, alpha, jpx.PremultipliedAlpha);
            }
            else if (stencil)
            {
                // JBIG2 stencil masks went through the own decoder above; for what remains (DCT), nothing is better than noise from raw data
                if (decoded.ImageFilter is not null) return null;
                if ((long)width * height > limits.Full)
                {
                    // large stencil mask without a row reader (rare filter chain): reduce from the whole data
                    using var all = ImageRowsFromArray(decoded.Data, rowBytes);
                    var (s, w, h) = ReducedStencil(all, width, height, rowBytes, inverted, limits.Full);
                    return new DecodedImage(w, h, s, ImagePixels.Alpha, stencilColor);
                }
                return Stencil(decoded.Data, width, height, inverted, stencilColor);
            }
            else if (decoded.ImageFilter is { Value: "DCTDecode" })
            {
                // JPEG through the own decoder (Stufe A, decided on 12.11.2026: one decoder for display, redaction and cutting, so
                // that a blacked-out copy looks exactly like the original outside the bar): the samples in the image's own components, large
                // ones already reduced, then on as every other image — colour space, /Decode, colour key. CMYK counts as amounts of colour,
                // inverted only through /Decode (ISO 32000-2, 8.9.5.2; S-A A26). Before, the codec of the display converted CMYK JPEGs itself
                // with a table baked from the colour space, and the copy blacked out in CMYK came out a few steps different everywhere.
                if (Jpeg(decoded, Get(dict, "Height", "H").AsNumber(), limits.Full) is not { } j) return null;
                (width, height) = (j.Width, j.Height);
                var own = j.Channels == space.Components;
                // grey and RGB in the plain device spaces without /Decode: the samples are the colours (grey stays one byte per pixel)
                var plain = own && decodeArray is null &&
                            (j.Channels == 1 && ReferenceEquals(space, ColorSpace.DeviceGray) || j.Channels == 3 && ReferenceEquals(space, ColorSpace.DeviceRgb));
                if (plain)
                {
                    if (opaque && j.Channels == 1) return new DecodedImage(width, height, j.Samples, ImagePixels.Gray);
                    rgba = Expand(j.Samples, width, height, j.Channels);
                    if (keyRanges is not null)
                    {
                        if (ReferenceEquals(rgba, j.Samples)) rgba = (byte[])rgba.Clone();
                        ApplyColorKey(rgba, j.Samples, width * height, j.Channels, j.Channels, keyRanges);
                    }
                }
                else
                {
                    var converter = new RowConverter(own ? space : ByCount(j.Channels, doc), 8, decodeArray);
                    if (opaque && converter.IsGray) return new DecodedImage(width, height, ConvertGray(j.Samples, width, height, converter), ImagePixels.Gray);
                    rgba = Convert(j.Samples, width, height, converter, own ? keyRanges : null);
                }
            }
            else if (decoded.ImageFilter is not null)
            {
                return null;   // JPEG 2000, CCITT and JBIG2 went through their own decoders above; no other image filter is known
            }
            else
            {
                if (bpc is not (1 or 2 or 4 or 8 or 16)) return null;
                var converter = new RowConverter(space, bpc, decodeArray);
                var gray = opaque && converter.IsGray;
                if ((long)width * height > limits.Full)
                {
                    // large, but a different filter chain: reduce from the whole data
                    using var all = ImageRowsFromArray(decoded.Data, rowBytes);
                    (rgba, width, height) = Reduced(all, width, height, rowBytes, converter, gray, limits.Full, keyRanges);
                    if (gray) return new DecodedImage(width, height, rgba, ImagePixels.Gray);
                }
                else if (gray)
                {
                    return new DecodedImage(width, height, ConvertGray(decoded.Data, width, height, converter), ImagePixels.Gray);
                }
                else
                {
                    // colour key mask: pixels whose raw values all lie within the ranges become transparent
                    rgba = Convert(decoded.Data, width, height, converter, keyRanges);
                }
            }
        }

        if (mask is not null)
        {
            // result as fine as the finer of image and mask (often the mask carries the shape, for example
            // text as an image on a presentation slide: 2 × 2 colour pixels with a 34,862 × 4,332 mask), capped
            var mw = (int)(Get(mask.Dictionary, "Width", "W").AsNumber() ?? 0);
            var mh = (int)(Get(mask.Dictionary, "Height", "H").AsNumber() ?? 0);
            if (mw > 0 && mh > 0 && mw <= MaxSide && mh <= MaxSide && (long)mw * mh <= MaxPixels)
            {
                var matte = mw == (int)(Get(dict, "Width", "W").AsNumber() ?? 0) && mh == (int)(Get(dict, "Height", "H").AsNumber() ?? 0)
                    ? Matte(mask.Dictionary, space) : null;
                var (tw, th) = MaskedSize(width, height, mw, mh, limits.Masked);
                if (tw != width || th != height)
                {
                    rgba = Resample(rgba, width, height, tw, th);
                    (width, height) = (tw, th);
                }
                if (MaskAlpha(mask, doc, width, height, limits) is { } alpha)
                    for (var i = 0; i < alpha.Length; i++)
                    {
                        var a = alpha[i];
                        if (matte is { } m && a is > 0 and < 255)
                        {
                            rgba[i * 4] = Unblend(rgba[i * 4], m.R, a);
                            rgba[i * 4 + 1] = Unblend(rgba[i * 4 + 1], m.G, a);
                            rgba[i * 4 + 2] = Unblend(rgba[i * 4 + 2], m.B, a);
                        }
                        rgba[i * 4 + 3] = (byte)(rgba[i * 4 + 3] * a / 255);
                    }
            }
        }

        return new DecodedImage(width, height, rgba);
    }

    private static ImageRowsArray ImageRowsFromArray(byte[] data, int rowBytes) => new(data, rowBytes);

    /// <summary>
    /// The matte colour of a soft mask (/Matte, ISO 32000-2, 11.6.5.2): the image colours are premultiplied with it. As many finite numbers as
    /// the image's space has components (an indexed space: its base, whose palette colours are premultiplied); null otherwise. The colour is
    /// unblended after the conversion to RGB — exact for DeviceGray and DeviceRGB (also as base of an indexed space), where the conversion is
    /// linear; an approximation for other spaces (docs/FEHLERGEDAECHTNIS.md, No. 562).
    /// </summary>
    private static Rgb? Matte(PdfDictionary mask, ColorSpace space)
    {
        if (mask.Get(PdfName.Get("Matte")) is not PdfArray values) return null;
        var target = space.IndexedBase ?? space;
        if (values.Count != target.Components || target.Components is < 1 or > 32) return null;
        var components = new double[values.Count];
        for (var i = 0; i < components.Length; i++)
            if (values.GetNumber(i) is { } v && double.IsFinite(v)) components[i] = v;
            else return null;
        return target.ToRgb(components);
    }

    /// <summary>c = m + (c′ − m) / α, bounded to the range (α between 0 and 1 exclusive; at 0 the colour is undefined and stays).</summary>
    private static byte Unblend(byte premultiplied, byte matte, byte alpha) =>
        (byte)Math.Clamp(Math.Round(matte + (premultiplied - matte) * 255.0 / alpha), 0, 255);

    /// <summary>JBIG2 to unfiltered 1-bit rows; shared segments from /JBIG2Globals.</summary>
    internal static StreamFilters.DecodeResult Jbig2(StreamFilters.DecodeResult decoded, PdfDocument doc, int width, int height)
    {
        var globals = decoded.ImageParms?.Get(PdfName.Get("JBIG2Globals")) is PdfStream g ? doc.DecodeStream(g).Data : null;
        var truncated = decoded.Truncated;
        var data = Filters.Jbig2.Jbig2Decoder.Decode(decoded.Data, globals, width, height, ref truncated);
        if (truncated && !decoded.Truncated) doc.AddProblem(Tr.T("Mindestens ein Datenstrom ist beschädigt und nur teilweise lesbar."));
        return new StreamFilters.DecodeResult(data, Truncated: truncated);
    }

    /// <summary>/SMaskInData of a JPEG 2000 image: 1 (opacity) or 2 (premultiplied opacity) in the data, otherwise 0.</summary>
    internal static int SMaskInData(PdfDictionary dict) => dict.GetInteger(PdfName.Get("SMaskInData")) switch { 1 => 1, 2 => 2, _ => 0 };

    /// <summary>Decodes JPEG 2000 (at most <paramref name="maxPixels"/>, otherwise reduced); null if broken.</summary>
    private static Filters.Jpx.JpxImage? Jpx(byte[] data, PdfDocument doc, bool applyPalette, Func<int, bool> ownColour, long maxPixels,
        double[]? indexDecode = null, int smaskInData = 0, int? declaredComponents = null)
    {
        try
        {
            var image = Filters.Jpx.JpxDecoder.Decode(data, maxPixels, applyPalette, ownColour, indexDecode, smaskInData, declaredComponents);
            if (image.Truncated) doc.AddProblem(Tr.T("Mindestens ein Datenstrom ist beschädigt und nur teilweise lesbar."));
            if (image.MissingFeatures.Length > 0)
                doc.AddProblem(Tr.T("Ein JPEG-2000-Bild verlangt zum Anzeigen etwas, das PDF-Knead nicht kann ({0}); es wird gezeigt, so weit es sich lesen lässt.",
                    Filters.Jpx.JpxRequirements.Describe(image.MissingFeatures)));
            return image;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException or AggregateException or InvalidOperationException)
        {
            // nonsensical values that no check anticipated: treated like broken data
            return null;
        }
    }

    /// <summary>JPEG 2000 stencil mask (/ImageMask): bright values are set bits; without /Decode [1 0] the value 0 paints.</summary>
    private static DecodedImage JpxStencil(Filters.Jpx.JpxImage jpx, bool inverted, uint color)
    {
        var alpha = new byte[(long)jpx.Width * jpx.Height];
        for (var i = 0; i < alpha.Length; i++)
        {
            var set = jpx.Samples[(long)i * jpx.Channels] >= 128;
            if (inverted ? set : !set) alpha[i] = 255;
        }
        return new DecodedImage(jpx.Width, jpx.Height, alpha, ImagePixels.Alpha, color);
    }

    /// <summary>Takes over the opacity from the image; premultiplied colours are converted back.</summary>
    private static void ApplyAlpha(byte[] rgba, byte[] alpha, bool premultiplied)
    {
        for (var i = 0; i < alpha.Length && i * 4 + 3 < rgba.Length; i++)
        {
            var a = alpha[i];
            if (premultiplied && a is > 0 and < 255)
                for (var k = 0; k < 3; k++) rgba[i * 4 + k] = (byte)Math.Min(255, rgba[i * 4 + k] * 255 / a);
            rgba[i * 4 + 3] = a;
        }
    }

    internal static PdfDictionary InlineFilters(PdfDictionary inline)
    {
        // inline images: /F and /DP instead of /Filter and /DecodeParms
        var d = new PdfDictionary();
        if (Get(inline, "Filter", "F") is { } f and not PdfNull) d.Set(PdfName.Filter, f);
        if (Get(inline, "DecodeParms", "DP") is { } p and not PdfNull) d.Set(PdfName.DecodeParms, p);
        return d;
    }

    /// <summary>
    /// The colour space of a JPEG 2000 image: /ColorSpace where it fits the channels, otherwise the colour specification of the JP2 wrapper
    /// (its ICC profile, or by the number of channels) — with the rendering intent and black point compensation of the image like any other
    /// space (docs/FEHLERGEDAECHTNIS.md, No. 592; before, the wrapper's space ignored /Intent).
    /// </summary>
    /// <summary>
    /// The colour space of a decoded JPEG 2000 image: the declared one if it fits the channels, otherwise the one the data gives (ICC profile,
    /// CIELab; grey, RGB or CMYK by the number of channels, ISO 32000-2, 7.4.9).
    /// </summary>
    internal static ColorSpace JpxSpace(ColorSpace? declared, Filters.Jpx.JpxImage jpx, PdfDocument doc, byte renderingIntent, byte blackPoint) =>
        declared is not null && declared.Components == jpx.Channels ? declared
            : jpx.Color == Filters.Jpx.JpxColor.Lab && jpx.SpaceObject() is { } lab ? ColorSpace.Create(lab, null, doc).WithRendering(renderingIntent, blackPoint)
            : JpxSpace(null, jpx.Channels, jpx is { Color: Filters.Jpx.JpxColor.Icc, IccProfile: { } icc } ? icc : null, doc, renderingIntent, blackPoint);

    internal static ColorSpace JpxSpace(ColorSpace? declared, int channels, byte[]? icc, PdfDocument doc, byte renderingIntent, byte blackPoint) =>
        declared is not null && declared.Components == channels ? declared
            : (icc is not null && ColorSpace.FromProfile(icc, channels, doc) is { } managed ? managed : ByCount(channels, doc)).WithRendering(renderingIntent, blackPoint);

    private static ColorSpace ByCount(int components, PdfDocument doc) => components switch
    {
        1 => ColorSpace.DeviceGray,
        4 => ColorSpace.DeviceCmykOf(doc),
        _ => ColorSpace.DeviceRgb,
    };

    /// <summary>
    /// A JPEG through the own decoder (<see cref="JpegDecoder"/>): /ColorTransform of its /DecodeParms, /Height for a header without lines,
    /// reduced to at most <paramref name="maxPixels"/>; null if the decoder refuses it (unsupported process, beyond the limits).
    /// </summary>
    internal static JpegImage? Jpeg(StreamFilters.DecodeResult decoded, double? pdfHeight, long maxPixels)
    {
        try
        {
            return JpegDecoder.Decode(decoded.Data, new JpegOptions((int?)decoded.ImageParms?.GetInteger(PdfName.Get("ColorTransform")),
                (long)Math.Clamp(pdfHeight ?? 0, 0, int.MaxValue), maxPixels, PdfLimits.Budget(4L << 30)));
        }
        catch (JpegException)
        {
            return null;
        }
    }

    /// <summary>RGBA from the codec → RGB (raw values for a colour space with three components).</summary>
    private static byte[] WithoutAlpha(byte[] rgba, int width, int height)
    {
        var rgb = new byte[(long)width * height * 3];
        for (long i = 0, s = 0; i < rgb.Length && s + 2 < rgba.Length; i += 3, s += 4)
        {
            rgb[i] = rgba[s];
            rgb[i + 1] = rgba[s + 1];
            rgb[i + 2] = rgba[s + 2];
        }
        return rgb;
    }

    // ------------------------------------------------------------ Conversion

    /// <summary>
    /// Converts a row of samples into RGBA: /Decode, colour space, a table for one component
    /// with ≤ 8 bits (separation colours, palettes), fast paths for 8-bit device colours. Created once per image
    /// and used for all rows (also from several threads at once).
    /// </summary>
    private sealed class RowConverter
    {
        private readonly ColorSpace _space;
        private readonly int _bpc;
        private readonly int _n;
        private readonly int _max;
        private readonly double[] _decode;
        private readonly bool _fast;
        private readonly bool _row8;
        private readonly Rgb[]? _table;
        private readonly byte[][]? _remap;
        private readonly bool _exact;

        /// <param name="exact">Every pixel through the colour space at its full depth, without the table to 8 bits that 16-bit images of a
        /// colour-managed space otherwise take for speed (a JPEG 2000 image in the wide working space; docs/FEHLERGEDAECHTNIS.md, No. 1092).</param>
        public RowConverter(ColorSpace space, int bpc, double[]? decodeArray, bool exact = false)
        {
            _space = space;
            _bpc = bpc;
            // 16 bits in a colour-managed space at their full depth, not first to 8 bits (ICC.1 6.5: 8000h is 32768/65535; before, 128/255 and
            // then the grid of the 8-bit path — up to 4 steps off; docs/FEHLERGEDAECHTNIS.md, No. 1126)
            _exact = exact || bpc == 16 && !ReferenceEquals(space, ColorSpace.DeviceRgb) && !ReferenceEquals(space, ColorSpace.DeviceGray) && space.HasRow8;
            exact = _exact;
            _n = Math.Max(space.Components, 1);
            _max = (1 << bpc) - 1;
            _decode = decodeArray is { } d && d.Length >= 2 * _n ? d : space.DefaultDecode(bpc);
            // a decode range for every component (a damaged colour space can give fewer than it has components): the missing ones 0 … 1
            if (_decode.Length < 2 * _n)
                _decode = [.. _decode[..(_decode.Length & ~1)], .. Enumerable.Range(0, _n - _decode.Length / 2).SelectMany(_ => (double[])[0, 1])];
            var isDefault = decodeArray is null || _decode.SequenceEqual(space.DefaultDecode(bpc));
            _fast = bpc == 8 && isDefault && (ReferenceEquals(space, ColorSpace.DeviceRgb) || ReferenceEquals(space, ColorSpace.DeviceGray));
            // colour-managed with three or four components (DeviceCMYK, ICC, CalRGB): whole rows through the tables of the colour space
            _row8 = !_fast && bpc == 8 && isDefault && _n >= 3 && space.HasRow8;
            if (!_fast && _n == 1 && bpc <= 8)
            {
                _table = new Rgb[_max + 1];
                for (var v = 0; v <= _max; v++) _table[v] = space.ToRgb([_decode[0] + v * (_decode[1] - _decode[0]) / _max]);
            }
            // /Decode or 16 bits with colour spaces that have a fast path: one table raw value → 8 bits per component, then on like an
            // ordinary 8-bit image (going through ToRgb per pixel is 3–4 times slower). Grey 8-bit images already have _table for this
            var plain = ReferenceEquals(space, ColorSpace.DeviceRgb) || ReferenceEquals(space, ColorSpace.DeviceGray);
            if (!exact && !_fast && !_row8 && bpc is 8 or 16 && !(_n == 1 && bpc == 8) && (plain || _n >= 3 && space.HasRow8)
                && IsUnit(space.DefaultDecode(8)))
            {
                _remap = new byte[_n][];
                for (var c = 0; c < _n; c++)
                {
                    var table = _remap[c] = new byte[_max + 1];
                    for (var v = 0; v <= _max; v++) table[v] = Rgb.ToByte(_decode[2 * c] + v * (_decode[2 * c + 1] - _decode[2 * c]) / _max);
                }
                _fast = plain;
                _row8 = !plain;
            }
        }

        private static bool IsUnit(double[] decode)
        {
            for (var i = 0; i + 1 < decode.Length; i += 2)
                if (decode[i] != 0 || decode[i + 1] != 1) return false;
            return true;
        }

        /// <summary>Row through <see cref="_remap"/> into ordinary 8-bit values (raw values for the colour key on the side).</summary>
        private void Remap(ReadOnlySpan<byte> row, int width, Span<byte> eight, Span<ushort> raw)
        {
            long bit = 0;
            for (int x = 0, i = 0; x < width; x++)
                for (var c = 0; c < _n; c++, i++, bit += _bpc)
                {
                    var v = ReadBits(row, bit, _bpc);
                    if (!raw.IsEmpty) raw[i] = (ushort)v;
                    eight[i] = _remap![c][v];
                }
        }

        public int Components => _n;

        public int RowBytes(int width) => (int)(((long)width * _n * _bpc + 7) / 8);

        /// <summary>Whether every pixel gives a grey (R = G = B); then 1 byte per pixel suffices instead of 4, with the same values.</summary>
        public bool IsGray => (_fast && _n == 1) || (_table is not null && Array.TrueForAll(_table, c => c.R == c.G && c.G == c.B));

        /// <summary>One row as grey values (only if <see cref="IsGray"/>): exactly the value <see cref="Convert"/> would write into R, G and B.</summary>
        public void ConvertGray(ReadOnlySpan<byte> row, int width, Span<byte> gray)
        {
            if (_remap is not null)
            {
                Remap(row, width, gray, []);   // grey only (one component): the table gives the value directly
                return;
            }
            if (_fast)
            {
                for (var x = 0; x < width; x++) gray[x] = At(row, x);
                return;
            }
            long bit = 0;
            for (var x = 0; x < width; x++, bit += _bpc) gray[x] = _table![ReadBits(row, bit, _bpc)].R;
        }

        /// <summary>Converts one row; <paramref name="raw"/> (if not empty) receives the raw values for the colour key.</summary>
        public void Convert(ReadOnlySpan<byte> row, int width, Span<byte> rgba, Span<ushort> raw)
        {
            if (_remap is not null)
            {
                var count = width * _n;
                var eight = System.Buffers.ArrayPool<byte>.Shared.Rent(count);
                try
                {
                    Remap(row, width, eight, raw);
                    if (_fast) Fast(eight.AsSpan(0, count), width, rgba, []);
                    else _space.ConvertRow8(eight.AsSpan(0, count), width, rgba);
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(eight);
                }
                return;
            }
            if (_fast)
            {
                Fast(row, width, rgba, raw);
                return;
            }
            if (_row8)
            {
                _space.ConvertRow8(row, width, rgba);
                if (!raw.IsEmpty)
                    for (var i = 0; i < width * _n; i++) raw[i] = At(row, i);
                return;
            }
            Slow(row, width, rgba, raw);
        }

        /// <summary>DeviceRGB/DeviceGray with 8 bits: take over the values.</summary>
        private void Fast(ReadOnlySpan<byte> row, int width, Span<byte> rgba, Span<ushort> raw)
        {
            for (int x = 0, s = 0, dst = 0; x < width; x++, s += _n, dst += 4)
            {
                if (_n == 1)
                {
                    var g = At(row, s);
                    rgba[dst] = rgba[dst + 1] = rgba[dst + 2] = g;
                }
                else
                {
                    rgba[dst] = At(row, s); rgba[dst + 1] = At(row, s + 1); rgba[dst + 2] = At(row, s + 2);
                }
                rgba[dst + 3] = 255;
                if (!raw.IsEmpty)
                    for (var c = 0; c < _n; c++) raw[x * _n + c] = At(row, s + c);
            }
        }

        /// <summary>Everything else: per pixel through the colour space (grey through the table).</summary>
        private void Slow(ReadOnlySpan<byte> row, int width, Span<byte> rgba, Span<ushort> raw)
        {
            Span<double> comps = stackalloc double[_n];
            long bit = 0;
            for (int x = 0, dst = 0; x < width; x++, dst += 4)
            {
                Rgb rgb;
                if (_table is not null)
                {
                    var v = ReadBits(row, bit, _bpc);
                    bit += _bpc;
                    if (!raw.IsEmpty) raw[x] = (ushort)v;
                    rgb = _table[v];
                }
                else
                {
                    for (var c = 0; c < _n; c++)
                    {
                        var v = ReadBits(row, bit, _bpc);
                        bit += _bpc;
                        if (!raw.IsEmpty) raw[x * _n + c] = (ushort)v;
                        comps[c] = _decode[2 * c] + v * (_decode[2 * c + 1] - _decode[2 * c]) / _max;
                    }
                    rgb = _exact ? _space.ToRgbExact(comps) : _space.ToRgb(comps);
                }
                rgba[dst] = rgb.R; rgba[dst + 1] = rgb.G; rgba[dst + 2] = rgb.B; rgba[dst + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Raw samples of a whole image at full size to RGBA, exactly as for drawing (/Decode, colour space, colour key) — for exporting
    /// images whose colours PNG cannot hold.
    /// </summary>
    internal static byte[] ToRgba(byte[] data, int width, int height, ColorSpace space, int bpc, double[]? decodeArray, double[]? colorKey) =>
        Convert(data, width, height, new RowConverter(space, bpc, decodeArray), colorKey);

    /// <summary>
    /// A converter of one row of raw samples to RGBA, exactly as for drawing; <c>raw</c> (if not null) receives the raw values for a
    /// colour key — for exporting images row by row.
    /// </summary>
    internal static Action<ReadOnlySpan<byte>, int, byte[], ushort[]?> RowToRgba(ColorSpace space, int bpc, double[]? decodeArray)
    {
        var converter = new RowConverter(space, bpc, decodeArray);
        return (row, width, rgba, raw) => converter.Convert(row, width, rgba, raw ?? Span<ushort>.Empty);
    }

    /// <summary>Converts the whole image; with <paramref name="colorKey"/> matching pixels become transparent.</summary>
    private static byte[] Convert(byte[] data, int width, int height, RowConverter converter, double[]? colorKey)
    {
        var n = converter.Components;
        if ((long)width * height * 4 > Array.MaxLength) throw new PdfLimitException("Bild");
        var rgba = new byte[width * height * 4];
        var rowBytes = converter.RowBytes(width);
        var keyed = colorKey is { } k && k.Length >= 2 * n;
        Bands(width, height, (y0, y1) =>
        {
            var raw = keyed ? new ushort[width * n] : null;
            for (var y = y0; y < y1; y++)
            {
                var start = (long)y * rowBytes;
                var row = start < data.Length ? data.AsSpan((int)start, (int)Math.Min(rowBytes, data.Length - start)) : [];
                var target = rgba.AsSpan(y * width * 4, width * 4);
                converter.Convert(row, width, target, raw);
                if (raw is null) continue;
                for (var x = 0; x < width; x++)
                {
                    var inside = true;
                    for (var c = 0; c < n && inside; c++)
                    {
                        var v = raw[x * n + c];
                        inside = v >= colorKey![2 * c] && v <= colorKey[2 * c + 1];
                    }
                    if (inside) target[x * 4 + 3] = 0;
                }
            }
        });
        return rgba;
    }

    /// <summary>
    /// Rows in bands, large images in parallel (CMYK through the colour profile, Lab and separation colours convert per pixel —
    /// single-threaded 12–35 million pixels per second). The converters have no mutable state, and the caches of the colour spaces
    /// are safe for concurrent use.
    /// </summary>
    private static void Bands(int width, int height, Action<int, int> rows)
    {
        if ((long)width * height < 1 << 20 || Environment.ProcessorCount == 1)
        {
            rows(0, height);
            return;
        }
        var band = Math.Max(16, (int)((1L << 18) / Math.Max(1, width)));
        Parallel.For(0, (height + band - 1) / band, b => rows(b * band, Math.Min(height, (b + 1) * band)));
    }

    /// <summary>The whole image as grey values (1 byte per pixel).</summary>
    private static byte[] ConvertGray(byte[] data, int width, int height, RowConverter converter)
    {
        var gray = new byte[(long)width * height];
        var rowBytes = converter.RowBytes(width);
        Bands(width, height, (y0, y1) =>
        {
            for (var y = y0; y < y1; y++)
            {
                var start = (long)y * rowBytes;
                var row = start < data.Length ? data.AsSpan((int)start, (int)Math.Min(rowBytes, data.Length - start)) : [];
                converter.ConvertGray(row, width, gray.AsSpan(y * width, width));
            }
        });
        return gray;
    }

    private static byte At(ReadOnlySpan<byte> data, int i) => (uint)i < (uint)data.Length ? data[i] : (byte)0;

    private static int ReadBits(ReadOnlySpan<byte> data, long bit, int bpc)
    {
        if (bpc == 8) return bit >> 3 < data.Length ? data[(int)(bit >> 3)] : 0;
        if (bpc == 16)
        {
            var i = (int)(bit >> 3);
            return i + 1 < data.Length ? (data[i] << 8 | data[i + 1]) : 0;
        }
        var byteIndex = bit >> 3;
        if (byteIndex >= data.Length) return 0;
        var shift = 8 - (int)(bit & 7) - bpc;
        return (data[(int)byteIndex] >> shift) & ((1 << bpc) - 1);
    }

    /// <summary>The codec already returned RGB or grey: only expand to RGBA (RGBA: unchanged, without a copy).</summary>
    /// <summary>
    /// Colour key on the 8-bit samples a codec delivered (<paramref name="stride"/> bytes per pixel, the first <paramref name="n"/> of them the
    /// components): pixels whose values all lie within the ranges become transparent (ISO 32000-2, 8.9.6.4; before, JPEGs kept them,
    /// docs/FEHLERGEDAECHTNIS.md, No. 764).
    /// </summary>
    private static void ApplyColorKey(byte[] rgba, byte[] samples, int pixels, int stride, int n, double[] ranges)
    {
        if (ranges.Length < 2 * n || (long)pixels * stride > samples.Length || (long)pixels * 4 > rgba.Length) return;
        for (var i = 0; i < pixels; i++)
        {
            var inside = true;
            for (var c = 0; c < n && inside; c++)
            {
                var v = samples[i * stride + c];
                inside = v >= ranges[2 * c] && v <= ranges[2 * c + 1];
            }
            if (inside) rgba[i * 4 + 3] = 0;
        }
    }

    private static byte[] Expand(byte[] samples, int width, int height, int components)
    {
        if (components == 4 && samples.Length == width * height * 4) return samples;
        var rgba = new byte[width * height * 4];
        for (int i = 0, s = 0; i < width * height; i++, s += components)
        {
            if (components == 1)
            {
                rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = At(samples, s);
            }
            else
            {
                rgba[i * 4] = At(samples, s); rgba[i * 4 + 1] = At(samples, s + 1); rgba[i * 4 + 2] = At(samples, s + 2);
            }
            rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }

    // ------------------------------------------------------------ Large images

    /// <summary>
    /// Reads the rows and averages each block of f × f pixels on the way. With a colour key, its pixels do not count towards the colour and
    /// the share of the others gives the opacity (before, large images lost their colour key; docs/FEHLERGEDAECHTNIS.md, No. 764).
    /// </summary>
    /// <param name="gray">Result as grey values (1 byte per pixel; only with <see cref="RowConverter.IsGray"/>).</param>
    /// <param name="colorKey">Ranges of the colour key on the raw values; null for none.</param>
    private static (byte[] Rgba, int Width, int Height) Reduced(IRowSource rows, int width, int height, int rowBytes, RowConverter converter,
        bool gray, long limit, double[]? colorKey = null)
    {
        var f = ReductionFactor(width, height, limit);
        int tw = (width + f - 1) / f, th = (height + f - 1) / f;
        if (gray) return (ReducedGray(rows, width, height, f, tw, th, rowBytes, converter), tw, th);
        var n = converter.Components;
        var raw = colorKey is { } key && key.Length >= 2 * n ? new ushort[(long)width * n] : null;
        var result = new byte[(long)tw * th * 4];
        var row = new byte[rowBytes];
        var rowRgba = new byte[width * 4];
        var sums = new int[tw * 3];
        var counts = new int[tw];
        var shown = new int[tw];
        var ended = new EndedRows();
        for (var ty = 0; ty < th; ty++)
        {
            if (ended.CopyPrevious(result, ty, tw * 4)) continue;
            Array.Clear(sums);
            Array.Clear(counts);
            Array.Clear(shown);
            for (var k = 0; k < f && ty * f + k < height; k++)
            {
                if (!ended.Read(rows, row)) Array.Clear(row);
                converter.Convert(row, width, rowRgba, raw);
                for (var x = 0; x < width; x++)
                {
                    var t = x / f;
                    counts[t]++;
                    if (raw is not null && Keyed(raw, x * n, n, colorKey!)) continue;
                    sums[t * 3] += rowRgba[x * 4];
                    sums[t * 3 + 1] += rowRgba[x * 4 + 1];
                    sums[t * 3 + 2] += rowRgba[x * 4 + 2];
                    shown[t]++;
                }
            }
            var dst = ty * tw * 4;
            for (var t = 0; t < tw; t++, dst += 4)
            {
                var c = Math.Max(1, shown[t]);
                result[dst] = (byte)(sums[t * 3] / c);
                result[dst + 1] = (byte)(sums[t * 3 + 1] / c);
                result[dst + 2] = (byte)(sums[t * 3 + 2] / c);
                result[dst + 3] = (byte)(255L * shown[t] / Math.Max(1, counts[t]));
            }
            ended.Done(ty);
        }
        return (result, tw, th);

        static bool Keyed(ushort[] values, int at, int n, double[] ranges)
        {
            for (var c = 0; c < n; c++)
                if (values[at + c] < ranges[2 * c] || values[at + c] > ranges[2 * c + 1]) return false;
            return true;
        }
    }

    /// <summary>Like <see cref="Reduced"/>, but 1 byte per pixel (average of the same grey values).</summary>
    private static byte[] ReducedGray(IRowSource rows, int width, int height, int f, int tw, int th, int rowBytes, RowConverter converter)
    {
        var result = new byte[(long)tw * th];
        var row = new byte[rowBytes];
        var rowGray = new byte[width];
        var sums = new int[tw];
        var counts = new int[tw];
        var ended = new EndedRows();
        for (var ty = 0; ty < th; ty++)
        {
            if (ended.CopyPrevious(result, ty, tw)) continue;
            Array.Clear(sums);
            Array.Clear(counts);
            for (var k = 0; k < f && ty * f + k < height; k++)
            {
                if (!ended.Read(rows, row)) Array.Clear(row);
                converter.ConvertGray(row, width, rowGray);
                for (var x = 0; x < width; x++)
                {
                    sums[x / f] += rowGray[x];
                    counts[x / f]++;
                }
            }
            for (var t = 0; t < tw; t++) result[ty * tw + t] = (byte)(sums[t] / Math.Max(1, counts[t]));
            ended.Done(ty);
        }
        return result;
    }

    /// <summary>Large stencil mask (1-bit scan as /ImageMask): coverage per block (1 byte per pixel; painted in the fill colour).</summary>
    private static (byte[] Alpha, int Width, int Height) ReducedStencil(IRowSource rows, int width, int height, int rowBytes, bool inverted, long limit)
    {
        var f = ReductionFactor(width, height, limit);
        int tw = (width + f - 1) / f, th = (height + f - 1) / f;
        var result = new byte[(long)tw * th];
        var row = new byte[rowBytes];
        var painted = new int[tw];
        var ended = new EndedRows();
        for (var ty = 0; ty < th; ty++)
        {
            if (ended.CopyPrevious(result, ty, tw)) continue;
            Array.Clear(painted);
            var rowsInBlock = Math.Min(f, height - ty * f);
            for (var k = 0; k < rowsInBlock; k++)
            {
                if (!ended.Read(rows, row)) row.AsSpan().Fill(inverted ? (byte)0 : (byte)0xFF);
                for (var x = 0; x < width; x++)
                {
                    var bitValue = (row[x >> 3] >> (7 - (x & 7))) & 1;
                    if (inverted ? bitValue == 1 : bitValue == 0) painted[x / f]++;
                }
            }
            for (var t = 0; t < tw; t++)
            {
                var cols = Math.Min(f, width - t * f);
                result[ty * tw + t] = (byte)(painted[t] * 255 / Math.Max(1, cols * rowsInBlock));
            }
            ended.Done(ty);
        }
        return (result, tw, th);
    }

    /// <summary>
    /// Data ended, image not yet: the remaining rows are all the same (empty). A block consisting only of such rows is computed once
    /// and then copied; otherwise 140 bytes with /Height 16,777,216 would compute every row individually (14 s per drawing).
    /// </summary>
    private sealed class EndedRows
    {
        private bool _ended;
        private bool _blockStartedEnded;
        private int _template = -1;

        public bool Read(IRowSource rows, byte[] row)
        {
            if (_ended) return false;
            if (rows.Read(row)) return true;
            _ended = true;
            return false;
        }

        /// <summary>At the start of a block: if an empty block has already been computed, copy it and return true.</summary>
        public bool CopyPrevious(byte[] result, int ty, int stride)
        {
            if (_template >= 0)
            {
                Array.Copy(result, (long)_template * stride, result, (long)ty * stride, stride);
                return true;
            }
            _blockStartedEnded = _ended;
            return false;
        }

        /// <summary>Block finished: if it consisted only of empty rows, it serves as the template.</summary>
        public void Done(int ty)
        {
            if (_blockStartedEnded) _template = ty;
        }
    }

    private static int ReductionFactor(int width, int height, long limit) =>
        Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)width * height / limit)));

    // ------------------------------------------------------------ Masks

    private static (int Width, int Height) MaskedSize(int width, int height, int maskWidth, int maskHeight, long masked)
    {
        long tw = Math.Max(width, maskWidth), th = Math.Max(height, maskHeight);
        var limit = Math.Max(masked, (long)width * height);
        if (tw * th > limit)
        {
            // reduce by an integer factor (as other readers do): every target pixel covers the same number of mask points; with odd
            // ratios an averaged dot screen would vary from pixel to pixel (residual pattern)
            var k = (long)Math.Ceiling(Math.Sqrt((double)tw * th / limit));
            tw = Math.Max(width, (tw + k - 1) / k);
            th = Math.Max(height, (th + k - 1) / k);
        }
        return ((int)tw, (int)th);
    }

    /// <summary>Resizes RGBA (nearest neighbour).</summary>
    private static byte[] Resample(byte[] rgba, int width, int height, int tw, int th)
    {
        var result = new byte[tw * th * 4];
        for (var y = 0; y < th; y++)
        {
            var sy = (int)((long)y * height / th);
            for (var x = 0; x < tw; x++)
            {
                var sx = (int)((long)x * width / tw);
                Buffer.BlockCopy(rgba, (sy * width + sx) * 4, result, (y * tw + x) * 4, 4);
            }
        }
        return result;
    }

    /// <summary>
    /// Opacity from a mask, sampled directly at the target size and read row by row, so that
    /// the (possibly huge) mask is never decoded completely. Soft mask: grey value (with /Decode);
    /// image mask (/Mask as a stream, ISO 32000-2, 8.9.6.3): visible where a stencil mask would paint.
    /// </summary>
    private static byte[]? MaskAlpha(PdfStream mask, PdfDocument doc, int tw, int th, Limits limits)
    {
        var d = mask.Dictionary;
        var mw = (int)(Get(d, "Width", "W").AsNumber() ?? 0);
        var mh = (int)(Get(d, "Height", "H").AsNumber() ?? 0);
        var stencil = d.GetBoolean(PdfName.Get("ImageMask")) ?? false;
        var bpc = stencil ? 1 : (int)(Get(d, "BitsPerComponent", "BPC").AsNumber() ?? 8);
        var spaceObj = Get(d, "ColorSpace", "CS");
        var gray = stencil || spaceObj is PdfNull || spaceObj is PdfName { Value: "DeviceGray" or "G" or "CalGray" };
        var rowBytes = (int)(((long)mw * Math.Clamp(bpc, 1, 16) + 7) / 8);

        IRowSource? rows = gray && bpc is 1 or 2 or 4 or 8 or 16 ? ImageRows.Open(doc, mask, rowBytes) : null;
        if (rows is null && gray && bpc is 1 or 2 or 4 or 8 or 16)
        {
            var decoded = doc.DecodeStream(mask, (int)Math.Min(int.MaxValue / 2, (long)rowBytes * mh));
            // JBIG2 (scanned plans: masks with hundreds of millions of pixels) stays packed instead of one byte per pixel through Alpha()
            if (decoded.ImageFilter is { Value: "JBIG2Decode" } && bpc == 1) decoded = Jbig2(decoded, doc, mw, mh);
            if (decoded.ImageFilter is null) rows = new ImageRowsArray(decoded.Data, rowBytes);
        }

        // rare cases (JPEG mask, colour mask, unusual bit depth): decode like an image
        if (rows is null)
        {
            if (Alpha(mask, doc, limits) is not { } old) return null;
            var next = 0;
            return Sampled(old.Width, old.Height, tw, th, columns => columns.FromBytes(old.Alpha.AsSpan(next++ * old.Width, old.Width)));
        }

        using (rows as IDisposable)
        {
            var decodeArray = PdfFunction.Numbers(Get(d, "Decode", "D") as PdfArray);
            var max = (1 << bpc) - 1;
            double d0 = 0, d1 = 1;
            if (decodeArray is { Length: >= 2 }) (d0, d1) = (decodeArray[0], decodeArray[1]);
            var table = new byte[bpc <= 8 ? max + 1 : 0];
            for (var v = 0; v < table.Length; v++)
            {
                var value = d0 + v * (d1 - d0) / max;
                // image mask: value 0 (after /Decode) is painted = visible
                table[v] = stencil ? (value < 0.5 ? (byte)255 : (byte)0) : (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
            }

            var row = new byte[rowBytes];
            // 1 bit (image masks, often huge): stays packed, the target columns count set bits
            if (bpc == 1)
                return Sampled(mw, mh, tw, th, columns =>
                {
                    if (!rows.Read(row)) Array.Clear(row);
                    columns.FromBits(row, table[0], table[1]);
                });
            var line = new byte[mw];
            return Sampled(mw, mh, tw, th, columns =>
            {
                if (!rows.Read(row)) Array.Clear(row);
                if (bpc == 8)
                {
                    for (var x = 0; x < mw; x++) line[x] = table[row[x]];
                }
                else
                {
                    for (var x = 0; x < mw; x++)
                    {
                        var v = ReadBits(row, (long)x * bpc, bpc);
                        line[x] = bpc <= 8 ? table[v] : (byte)Math.Clamp(Math.Round((d0 + v * (d1 - d0) / max) * 255), 0, 255);
                    }
                }
                columns.FromBytes(line);
            });
        }
    }

    /// <summary>
    /// Reduces one byte per pixel (grey or coverage) by area to <paramref name="tw"/> × <paramref name="th"/>;
    /// <paramref name="nextLine"/> fills one row (<paramref name="width"/> values) at a time, in order. For drawing screened
    /// black-and-white scans (without averaging by area, moiré appears).
    /// </summary>
    /// <param name="width">Source width in pixels.</param>
    /// <param name="height">Source height in pixels; <paramref name="nextLine"/> is called once per source row.</param>
    /// <param name="tw">Target width.</param>
    /// <param name="th">Target height.</param>
    /// <param name="nextLine">Fills the passed buffer with the next source row.</param>
    /// <returns>The target image, one byte per pixel; targets larger than the source are filled by nearest neighbour.</returns>
    public static byte[] AreaReduce(int width, int height, int tw, int th, Action<byte[]> nextLine)
    {
        var line = new byte[width];
        return Sampled(width, height, tw, th, columns =>
        {
            nextLine(line);
            columns.FromBytes(line);
        });
    }

    /// <summary>
    /// Opacity at the target size from mask rows that <paramref name="nextRow"/> places into <see cref="MaskColumns"/> one after another.
    /// Reducing averages the whole block, because masks are often dot screens for half opacity (issue11518: ten times finer than the
    /// image); a single point per target pixel would hit only set or only empty points depending on the phase (stripes, moiré). The
    /// average is by area: each mask row counts proportionally towards the (at most two) target rows it covers. Enlarging: nearest neighbour.
    /// </summary>
    private static byte[] Sampled(int mw, int mh, int tw, int th, Action<MaskColumns> nextRow)
    {
        var result = new byte[tw * th];
        var columns = new MaskColumns(mw, tw);
        var row = columns.Row;
        var columnWeight = columns.Weight;

        if (th >= mh)
        {
            var current = -1;
            for (var y = 0; y < th; y++)
            {
                var my = (int)((long)y * mh / th);
                while (current < my)
                {
                    nextRow(columns);
                    current++;
                }
                for (var x = 0; x < tw; x++) result[y * tw + x] = (byte)((row[x] + columnWeight / 2) / columnWeight);
            }
            return result;
        }

        // row my covers [my·th, (my+1)·th), target row r covers [r·mh, (r+1)·mh)
        var acc = new long[tw];
        var next = new long[tw];
        var weight = columnWeight * mh;
        for (var my = 0; my < mh; my++)
        {
            nextRow(columns);
            var start = (long)my * th;
            var r = (int)(start / mh);
            var first = Math.Min(start + th, (long)(r + 1) * mh) - start;
            for (var x = 0; x < tw; x++)
            {
                acc[x] += row[x] * first;
                if (first < th) next[x] += row[x] * (th - first);
            }
            if (start + th >= (long)(r + 1) * mh)
            {
                for (var x = 0; x < tw; x++) result[r * tw + x] = (byte)((acc[x] + weight / 2) / weight);
                (acc, next) = (next, acc);
                Array.Clear(next);
            }
        }
        return result;
    }

    /// <summary>
    /// One mask row in target columns (<see cref="Row"/>, each column with weight <see cref="Weight"/>). Reduced by area:
    /// mask column mx covers [mx·tw, (mx+1)·tw), target column t covers [t·mw, (t+1)·mw); with whole columns, odd ratios
    /// (2000 → 1414) would again leave single full and empty values. Enlarged: nearest neighbour.
    /// </summary>
    private sealed class MaskColumns
    {
        private readonly int _mw, _tw;
        private readonly bool _down;
        private readonly int[] _target = [], _share = [];   // per mask column: first target column, share in it
        // per target column: fully covered columns _whole[t] … _wholeEnd[t] − 1, plus at most two partly covered ones with their share (−1: none)
        private readonly int[] _whole = [], _wholeEnd = [], _left = [], _leftShare = [], _right = [], _rightShare = [];

        public MaskColumns(int mw, int tw)
        {
            (_mw, _tw, _down) = (mw, tw, tw < mw);
            Row = new long[tw];
            if (!_down) return;
            _target = new int[mw];
            _share = new int[mw];
            for (var mx = 0; mx < mw; mx++)
            {
                var start = (long)mx * tw;
                _target[mx] = (int)(start / mw);
                _share[mx] = (int)(Math.Min(start + tw, (long)(_target[mx] + 1) * mw) - start);
            }
            _whole = new int[tw];
            _wholeEnd = new int[tw];
            _left = new int[tw];
            _leftShare = new int[tw];
            _right = new int[tw];
            _rightShare = new int[tw];
            for (var t = 0; t < tw; t++)
            {
                long from = (long)t * mw, to = (long)(t + 1) * mw;
                var first = (int)(from / tw);
                var last = (int)((to - 1) / tw);
                var firstShare = (int)(Math.Min((long)(first + 1) * tw, to) - from);
                var lastShare = (int)(to - (long)last * tw);
                // fully covered edge columns belong to the counted range (with an integer ratio: only whole bytes)
                (_left[t], _leftShare[t]) = firstShare < tw ? (first, firstShare) : (-1, 0);
                (_right[t], _rightShare[t]) = last > first && lastShare < tw ? (last, lastShare) : (-1, 0);
                _whole[t] = firstShare < tw ? first + 1 : first;
                _wholeEnd[t] = last > first && lastShare < tw ? last : last + 1;
                if (last == first && firstShare < tw) _wholeEnd[t] = _whole[t];
            }
        }

        public long[] Row { get; }

        public long Weight => _down ? _mw : 1;

        public void FromBytes(ReadOnlySpan<byte> line)
        {
            if (!_down)
            {
                for (var x = 0; x < _tw; x++) Row[x] = line[(int)((long)x * _mw / _tw)];
                return;
            }
            Array.Clear(Row);
            for (var mx = 0; mx < _mw; mx++)
            {
                var v = line[mx];
                if (v == 0) continue;
                var t = _target[mx];
                var first = _share[mx];
                Row[t] += v * first;
                if (first < _tw) Row[t + 1] += v * (_tw - first);
            }
        }

        /// <summary>Packed 1-bit row (most significant bit first); <paramref name="zero"/>, <paramref name="one"/>: opacity of the bit values.</summary>
        public void FromBits(byte[] packed, byte zero, byte one)
        {
            if (!_down)
            {
                for (var x = 0; x < _tw; x++) Row[x] = Bit(packed, (int)((long)x * _mw / _tw)) ? one : zero;
                return;
            }
            for (var t = 0; t < _tw; t++)
            {
                var sum = 0L;
                if (_left[t] >= 0) sum += (long)(Bit(packed, _left[t]) ? one : zero) * _leftShare[t];
                if (_right[t] >= 0) sum += (long)(Bit(packed, _right[t]) ? one : zero) * _rightShare[t];
                var whole = _wholeEnd[t] - _whole[t];
                if (whole > 0)
                {
                    var ones = CountBits(packed, _whole[t], _wholeEnd[t]);
                    sum += ((long)ones * one + (long)(whole - ones) * zero) * _tw;
                }
                Row[t] = sum;
            }
        }

        private static bool Bit(byte[] packed, int i) => (packed[i >> 3] >> (7 - (i & 7)) & 1) != 0;

        /// <summary>Set bits from <paramref name="from"/> up to before <paramref name="to"/> (edge bytes through masks, 64 bits at a time in between).</summary>
        private static int CountBits(byte[] packed, int from, int to)
        {
            int head = from >> 3, tail = (to - 1) >> 3;
            var headMask = 0xFF >> (from & 7);
            var tailMask = 0xFF << (7 - ((to - 1) & 7)) & 0xFF;
            if (head == tail) return BitOperations.PopCount((uint)(packed[head] & headMask & tailMask));
            var n = BitOperations.PopCount((uint)(packed[head] & headMask)) + BitOperations.PopCount((uint)(packed[tail] & tailMask));
            var i = head + 1;
            for (; i + 8 <= tail; i += 8) n += BitOperations.PopCount(MemoryMarshal.Read<ulong>(packed.AsSpan(i)));
            for (; i < tail; i++) n += BitOperations.PopCount((uint)packed[i]);
            return n;
        }
    }

    /// <summary>
    /// Stencil mask: 1 bit; painted positions fully covered, the rest transparent. 1 byte per pixel:
    /// the renderer adds the fill colour when drawing (instead of 4 bytes per pixel with the colour baked in).
    /// </summary>
    private static DecodedImage Stencil(byte[] data, int width, int height, bool inverted, uint color)
    {
        var alpha = new byte[(long)width * height];
        var rowBytes = (width + 7) / 8;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = y * rowBytes + (x >> 3);
            var bitValue = i < data.Length ? (data[i] >> (7 - (x & 7))) & 1 : 1;
            // without /Decode [1 0] the value 0 paints
            if (inverted ? bitValue == 1 : bitValue == 0) alpha[y * width + x] = 255;
        }
        return new DecodedImage(width, height, alpha, ImagePixels.Alpha, color);
    }

    /// <summary>Alpha from a mask image (greyscale or image mask), at the mask's own decoded size.</summary>
    internal static (int Width, int Height, byte[] Alpha)? Alpha(PdfStream mask, PdfDocument doc) => Alpha(mask, doc, Display);

    private static (int Width, int Height, byte[] Alpha)? Alpha(PdfStream mask, PdfDocument doc, Limits limits)
    {
        var d = mask.Dictionary;
        var isStencil = d.GetBoolean(PdfName.Get("ImageMask")) ?? false;
        var decoded = isStencil
            ? DecodeCore(d, mask, null, null, doc, true, 0xFF000000, allowMask: false, limits)   // image mask: painted = visible
            : DecodeCore(d, mask, null, null, doc, false, 0, allowMask: false, limits);
        if (decoded is null) return null;
        // image mask (/Mask as a stream): visible where the stencil mask would paint (bit 0, ISO 32000-2, 8.9.6.3);
        // soft mask: grey value = opacity. Coverage and grey are already 1 byte per pixel.
        if (decoded.Format != ImagePixels.Rgba) return (decoded.Width, decoded.Height, decoded.Pixels);
        var alpha = new byte[decoded.Width * decoded.Height];
        for (var i = 0; i < alpha.Length; i++)
            alpha[i] = isStencil ? decoded.Pixels[i * 4 + 3] : decoded.Pixels[i * 4];
        return (decoded.Width, decoded.Height, alpha);
    }
}

/// <summary>Raw image data row by row: from the data stream (<see cref="ImageRows"/>) or from a decoded array.</summary>
internal interface IRowSource
{
    bool Read(Span<byte> row);
}

/// <summary>Rows from data that is already decoded. <c>missing</c>: the byte for what the data lacks (for a 1-bit mask set bits, as the display reads it).</summary>
internal sealed class ImageRowsArray(byte[] data, int rowBytes, byte missing = 0) : IRowSource, IDisposable
{
    private long _offset;

    public bool Read(Span<byte> row)
    {
        row = row[..rowBytes];
        if (_offset >= data.Length) return false;
        var available = (int)Math.Min(rowBytes, data.Length - _offset);
        data.AsSpan((int)_offset, available).CopyTo(row);
        row[available..].Fill(missing);
        _offset += rowBytes;
        return true;
    }

    public void Dispose()
    {
    }
}
