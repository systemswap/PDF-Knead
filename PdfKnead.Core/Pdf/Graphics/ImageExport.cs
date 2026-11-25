using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Graphics;

/// <summary>File format of an exported image.</summary>
public enum ImageExportFormat
{
    /// <summary>JPEG, the bytes from the PDF unchanged.</summary>
    Jpeg,

    /// <summary>JPEG 2000 (JP2 file or bare codestream), the bytes from the PDF unchanged.</summary>
    Jpeg2000,

    /// <summary>PNG, lossless.</summary>
    Png,

    /// <summary>TIFF with CMYK, lossless.</summary>
    Tiff,
}

/// <summary>An image of a page as a file of its own (see <see cref="ImageExport"/>).</summary>
/// <param name="Format">The file format.</param>
/// <param name="Data">The file.</param>
/// <param name="Extension">File extension with the dot (".jpg", ".jp2", ".j2k", ".png", ".tif").</param>
/// <param name="MaskPng">The opacity as a greyscale PNG (white = opaque) when it cannot go into <paramref name="Data"/>: beside JPEG and
/// JPEG 2000, or when the mask has a different size than the image; otherwise null.</param>
/// <param name="ColorsConverted">The colour space has no equivalent in the file format (Lab, spot colours, unusual /Decode …): the colours
/// were converted to RGB the way they are drawn.</param>
/// <param name="Stencil">A stencil mask (/ImageMask): painted places black, the rest white.</param>
public sealed record ExportedImage(ImageExportFormat Format, byte[] Data, string Extension, byte[]? MaskPng, bool ColorsConverted, bool Stencil);

/// <summary>What saving an image will write, known before anything is decoded (see <see cref="ImageExport.Plan"/>).</summary>
/// <param name="Format">The file format.</param>
/// <param name="Extension">File extension with the dot.</param>
/// <param name="MaskBeside">The opacity goes into a greyscale PNG of its own.</param>
/// <param name="ColorsConverted">The colours are converted to RGB the way they are drawn.</param>
/// <param name="Stencil">A stencil mask: painted places black, the rest white.</param>
public sealed record ImageExportPlan(ImageExportFormat Format, string Extension, bool MaskBeside, bool ColorsConverted, bool Stencil);

/// <summary>
/// Takes an image out of a PDF in its original quality (image info: „Bild speichern“): JPEG and JPEG 2000 byte for byte as stored,
/// everything else losslessly — grey, RGB and palette images as PNG in their own bit depth (1, 2, 4, 8, 16), CMYK as TIFF, each with
/// its ICC profile. An opacity mask of the same size becomes the alpha channel, otherwise a PNG of its own. Flate and unfiltered data are
/// read and written row by row, so a large scan or plan needs memory for a few rows, not for the whole image.
/// </summary>
public static class ImageExport
{
    private const int MaxIccStream = 4 << 20;

    private static readonly PdfName ColorSpaceKey = PdfName.Get("ColorSpace");

    private enum Kind { Jpeg, Jpeg2000, Stencil, Gray, Rgb, Cmyk, Indexed, Converted }

    /// <summary>Everything about an image that decides how it is written.</summary>
    private sealed record Setup(Kind Kind, int Width, int Height, int Bpc, int Components, int RowBytes, PdfDictionary Dict, ColorSpace? Space,
        double[]? Decode, bool InvertGray, byte[]? Profile, (byte[] Rgb, bool Converted, byte[]? Profile)? Palette, double[]? ColorKey,
        PdfStream? Mask, bool MaskInside, string Extension, double[]? Matte = null);

    /// <summary>What <see cref="Write"/> will write for the image, without decoding it.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="source">The image.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="PdfException">The image cannot be saved (no valid size, unsupported bit depth, too large).</exception>
    public static ImageExportPlan Plan(PdfDocument doc, ImageSource source) => PlanOf(Analyse(doc, source));

    private static ImageExportPlan PlanOf(Setup s) => new(
        s.Kind switch { Kind.Jpeg => ImageExportFormat.Jpeg, Kind.Jpeg2000 => ImageExportFormat.Jpeg2000, Kind.Cmyk => ImageExportFormat.Tiff, _ => ImageExportFormat.Png },
        s.Extension, s.Mask is not null && !s.MaskInside, s.Kind == Kind.Converted || s.Palette is { Converted: true }, s.Kind == Kind.Stencil);

    /// <summary>Writes an image of the display list in its original quality.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="source">The image.</param>
    /// <param name="output">Where the image file goes.</param>
    /// <param name="maskOutput">Where the opacity goes when it does not fit into the image file (<see cref="ImageExportPlan.MaskBeside"/>);
    /// called at most once; null = not written.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="PdfException">The image data cannot be read, or the image cannot be saved.</exception>
    /// <param name="drawn">The size of the image as drawn on its page in points (the lengths of its axes), or null: it gives the resolution of
    /// a file that notes one (TIFF; FG 1019).</param>
    public static ImageExportPlan Write(PdfDocument doc, ImageSource source, Stream output, Func<Stream>? maskOutput, (double Width, double Height)? drawn = null)
    {
        var s = Analyse(doc, source);
        var plan = PlanOf(s);
        if (plan.MaskBeside && maskOutput is not null) WriteMask(doc, s.Mask!, maskOutput);
        if (s.Kind is Kind.Jpeg or Kind.Jpeg2000)
        {
            var decoded = Decoded(doc, source, s);
            // a JPEG as a file of its own: what the PDF says about it (/ColorTransform, the number of lines) goes into the file, so that
            // other programs read it as the PDF means it (FG 457)
            output.Write(s.Kind == Kind.Jpeg ? JpegHeader.Prepare(decoded.Data, decoded.ImageParms, s.Height) : decoded.Data);
            return plan;
        }
        using var rows = OpenRows(doc, source, s);
        using var alpha = s.MaskInside ? OpenAlpha(doc, s) : null;
        switch (s.Kind)
        {
            case Kind.Stencil:
                // bit 0 paints, with /Decode [1 0] bit 1 (8.9.6.2): painted places become black
                PngEncoder.Encode(s.Width, s.Height, 1, PngColorType.Gray, Packed(rows, s.Decode is { Length: >= 2 } d && d[0] > d[1]), output);
                break;
            case Kind.Gray:
                if (alpha is null && s.ColorKey is null)
                    PngEncoder.Encode(s.Width, s.Height, s.Bpc, PngColorType.Gray, Packed(rows, s.InvertGray), output, iccProfile: s.Profile);
                else
                {
                    // grey with alpha exists only with 8 or 16 bits: smaller depths are spread to 8 bits (every value stays distinct)
                    var depth = s.Bpc == 16 ? 16 : 8;
                    PngEncoder.Encode(s.Width, s.Height, depth, PngColorType.GrayAlpha, Channels(s, rows, alpha, depth, s.InvertGray), output, iccProfile: s.Profile,
                        significantBits: SignificantBits(s, alpha, depth, 1));
                }
                break;
            case Kind.Rgb:
            {
                var depth = s.Bpc == 16 ? 16 : 8;
                var withAlpha = alpha is not null || s.ColorKey is not null;
                var bits = SignificantBits(s, alpha, depth, 3);
                if (depth == 16)
                {
                    PngEncoder.Encode(s.Width, s.Height, 16, withAlpha ? PngColorType.Rgba : PngColorType.Rgb, Channels(s, rows, alpha, 16, false),
                        output, iccProfile: s.Profile, significantBits: bits);
                    break;
                }
                // at most 256 colours: a palette image (RFC 2083, 9.6)
                using var again = new Reopener(doc, source, s, rows, alpha);
                PngEncoder.EncodeColour(s.Width, s.Height, withAlpha, () =>
                {
                    var (r, a) = again.Next();
                    return Channels(s, r, a, 8, false);
                }, output, s.Profile, significantBits: bits);
                break;
            }
            case Kind.Cmyk:
            {
                var depth = s.Bpc == 16 ? 16 : 8;
                TiffEncoder.EncodeCmyk(s.Width, s.Height, depth, alpha is not null || s.ColorKey is not null, Channels(s, rows, alpha, depth, false),
                    s.Profile ?? CmykProfile(doc), output, Resolution(s, drawn));
                break;
            }
            case Kind.Indexed:
            {
                using var again = new Reopener(doc, source, s, rows, alpha);
                WriteIndexed(s, again, output);
                break;
            }
            default:
            {
                using var again = new Reopener(doc, source, s, rows, alpha);
                WriteConverted(s, again, output);
                break;
            }
        }
        return plan;
    }

    /// <summary>Exports an image of the display list into memory (see <see cref="Write"/>).</summary>
    /// <param name="doc">The document.</param>
    /// <param name="source">The image.</param>
    /// <returns>The file.</returns>
    /// <exception cref="PdfException">The image data cannot be read, or the image cannot be saved.</exception>
    public static ExportedImage Export(PdfDocument doc, ImageSource source, (double Width, double Height)? drawn = null)
    {
        using var output = new MemoryStream();
        MemoryStream? mask = null;
        var plan = Write(doc, source, output, () => mask = new MemoryStream(), drawn);
        using (mask)
            return new ExportedImage(plan.Format, output.ToArray(), plan.Extension, mask?.ToArray(), plan.ColorsConverted, plan.Stencil);
    }

    /// <summary>
    /// Whether a file of the JPEG 2000 family is a JP2 file or JP2-compatible: the File Type box right after the signature box has the brand
    /// „jp2 “ or names it in its compatibility list (ISO/IEC 15444-1, I.5.2; ISO/IEC 15444-2, M.2.1). Without a readable File Type box: yes,
    /// as before.
    /// </summary>
    internal static bool Jp2Compatible(ReadOnlySpan<byte> file)
    {
        if (file.Length < 32 || !file[16..20].SequenceEqual("ftyp"u8)) return true;
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(file[12..]);
        if (length < 20 || 12 + length > (uint)file.Length) return true;
        if (file[20..24].SequenceEqual("jp2 "u8)) return true;
        for (var at = 28; at + 4 <= 12 + (int)length; at += 4)
            if (file[at..(at + 4)].SequenceEqual("jp2 "u8)) return true;
        return false;
    }

    /// <summary>An image inserted in PDF Knead (element): its pixels as PNG, RGB or RGBA.</summary>
    /// <param name="image">The pixels.</param>
    /// <returns>The file.</returns>
    public static ExportedImage Export(RgbaImage image)
    {
        var opaque = true;
        for (var i = 3; i < image.Pixels.Length && opaque; i += 4) opaque = image.Pixels[i] == 255;
        int w = image.Width, h = image.Height;
        using var output = new MemoryStream();
        // at most 256 colours: a palette image (RFC 2083, 9.6)
        PngEncoder.EncodeColour(w, h, !opaque, () => (y, row) =>
        {
            var src = image.Pixels.AsSpan(y * w * 4, w * 4);
            if (!opaque) src.CopyTo(row);
            else
                for (var x = 0; x < w; x++)
                {
                    row[x * 3] = src[x * 4];
                    row[x * 3 + 1] = src[x * 4 + 1];
                    row[x * 3 + 2] = src[x * 4 + 2];
                }
        }, output);
        return new ExportedImage(ImageExportFormat.Png, output.ToArray(), ".png", null, false, false);
    }

    // ------------------------------------------------------------ analysis

    private static Setup Analyse(PdfDocument doc, ImageSource source)
    {
        var dict = source.Dictionary;
        var width = (int)(Get(dict, "Width", "W").AsNumber() ?? 0);
        var height = (int)(Get(dict, "Height", "H").AsNumber() ?? 0);
        if (width <= 0 || height <= 0 || width > PdfImageDecoder.MaxSide || height > PdfImageDecoder.MaxSide || (long)width * height > PdfImageDecoder.MaxPixels)
            throw new PdfException(Tr.T("Das Bild hat keine gültige Größe."));
        var stencil = source.IsStencil || Get(dict, "ImageMask", "IM") is PdfBoolean { Value: true };
        var bpc = stencil ? 1 : (int)(Get(dict, "BitsPerComponent", "BPC").AsNumber() ?? 8);
        var spaceObj = Resolve(Get(dict, "ColorSpace", "CS"), source.Resources);
        var decode = PdfFunction.Numbers(Get(dict, "Decode", "D") as PdfArray);
        var space = stencil ? ColorSpace.DeviceGray : spaceObj is PdfNull ? null : ColorSpace.Create(spaceObj, source.Resources, doc);
        var n = Math.Max(space?.Components ?? 1, 1);
        var rowBytes = ((long)width * n * Math.Clamp(bpc, 1, 16) + 7) / 8;
        if (rowBytes > int.MaxValue / 8) throw new PdfLimitException(Tr.T("Das Bild ist zu groß zum Speichern ({0} × {1} Pixel).", width, height));
        var chain = source.Stream is not null ? StreamFilters.Chain(dict) : StreamFilters.Chain(PdfImageDecoder.InlineFilters(dict));
        var last = chain.Count > 0 ? chain[^1].Name : null;

        // opacity: /SMask wins over /Mask (ISO 32000-2, 8.9.5.1); masks have no masks of their own
        var mask = stencil ? null : dict.Get(PdfName.Get("SMask")) as PdfStream ?? dict.Get(PdfName.Get("Mask")) as PdfStream;
        var maskSameSize = mask is not null && (int)(Get(mask.Dictionary, "Width", "W").AsNumber() ?? 0) == width
                                            && (int)(Get(mask.Dictionary, "Height", "H").AsNumber() ?? 0) == height;
        var colorKey = !stencil && mask is null && dict.Get(PdfName.Get("Mask")) is PdfArray key && PdfFunction.Numbers(key) is { } k && k.Length >= 2 * n ? k : null;

        if (last is "DCTDecode") return new Setup(Kind.Jpeg, width, height, bpc, n, (int)rowBytes, dict, space, decode, false, null, null, null, mask, false, ".jpg");
        if (last is "JPXDecode")
        {
            // a file of the JPEG 2000 family (signature box, ISO/IEC 15444-1, I.5.1) or a bare codestream (.j2k); a JPX file .jpf unless its
            // compatibility list names „jp2 “ (ISO/IEC 15444-2, M.2.1; FG 988: before .jp2)
            var head = Decoded(doc, source, null).Data;
            var file = head.AsSpan().StartsWith((ReadOnlySpan<byte>)[0, 0, 0, 12, 0x6A, 0x50, 0x20, 0x20]);
            return new Setup(Kind.Jpeg2000, width, height, bpc, n, (int)rowBytes, dict, space, decode, false, null, null, null, mask, false,
                !file ? ".j2k" : Jp2Compatible(head) ? ".jp2" : ".jpf");
        }
        // a depth between the allowed ones is read leniently (1–16 bits, S-A A192) and saved converted as displayed (docs/FEHLERGEDAECHTNIS.md, No. 1134)
        if (!ImageDepth.Readable(bpc)) throw new PdfException(Tr.T("Bilder mit {0} Bit je Farbe lassen sich nicht speichern.", bpc));
        // a mask of another size cannot become the alpha channel without resampling: it is saved beside the image
        if (stencil) return new Setup(Kind.Stencil, width, height, 1, 1, (int)rowBytes, dict, space, decode, false, null, null, null, null, false, ".png");

        var family = Family(spaceObj);
        var profile = ProfileOf(doc, spaceObj, family);
        var profileSpace = ProfileSpace(profile);
        var plainDecode = decode is null || space is not null && decode.SequenceEqual(space.DefaultDecode(bpc));
        var invertedGray = n == 1 && decode is [1, 0];
        var kind = Kind.Converted;
        (byte[] Rgb, bool Converted, byte[]? Profile)? palette = null;
        switch (ImageDepth.Allowed(bpc) ? family : null)
        {
            case "DeviceGray" or "CalGray" or "ICCBased" when n == 1 && (plainDecode || invertedGray) && (profile is null || profileSpace == "GRAY"):
                kind = Kind.Gray;
                break;
            case "DeviceRGB" or "CalRGB" or "ICCBased" when n == 3 && plainDecode && (profile is null || profileSpace == "RGB "):
                kind = Kind.Rgb;
                break;
            case "DeviceCMYK" or "ICCBased" when n == 4 && plainDecode && (profile is null || profileSpace == "CMYK"):
                kind = Kind.Cmyk;
                break;
            // an Indexed space is an array (base, highest index, lookup); a bare name /Indexed has no palette — converted like any other
            case "Indexed" when bpc <= 8 && plainDecode && spaceObj is PdfArray indexed && space is not null &&
                                Palette(doc, indexed, space, source.Resources) is { } p:
                kind = Kind.Indexed;
                palette = p;
                profile = p.Profile;
                break;
        }
        if (kind == Kind.Converted) profile = null;
        // a soft mask of the same size with /Matte: the colours are premultiplied with it (ISO 32000-2, 11.6.5.3); PNG keeps them
        // unpremultiplied (RFC 2083, 2.4, 9.4), so they are unblended on export (FG 1014)
        var matte = maskSameSize && dict.Get(PdfName.Get("SMask")) is PdfStream && space is not null ? MatteOf(mask!.Dictionary, space) : null;
        return new Setup(kind, width, height, bpc, n, (int)rowBytes, dict, space ?? ColorSpace.DeviceGray, decode, invertedGray, profile, palette, colorKey,
            mask, maskSameSize, kind == Kind.Cmyk ? ".tif" : ".png", matte);
    }

    private static StreamFilters.DecodeResult Decoded(PdfDocument doc, ImageSource source, Setup? s)
    {
        var hint = s is null ? 0 : (int)Math.Min(int.MaxValue / 2, (long)s.RowBytes * s.Height);
        if (s is not null && (long)s.RowBytes * s.Height > Array.MaxLength && s.Kind is not (Kind.Jpeg or Kind.Jpeg2000))
            throw new PdfLimitException(Tr.T("Das Bild ist zu groß zum Speichern ({0} × {1} Pixel).", s.Width, s.Height));
        var decoded = source.Stream is { } stream
            ? doc.DecodeStream(stream, hint)
            : StreamFilters.Decode(source.Inline?.Data ?? [], PdfImageDecoder.InlineFilters(source.Dictionary), hint);
        if (s is not null && decoded.ImageFilter is { Value: "JBIG2Decode" }) decoded = PdfImageDecoder.Jbig2(decoded, doc, s.Width, s.Height);
        return decoded;
    }

    // ------------------------------------------------------------ rows

    /// <summary>The rows of an image in order: from the stream row by row where its filters allow it, otherwise from the decoded data.</summary>
    private abstract class Rows : IDisposable
    {
        /// <summary>Row <paramref name="y"/>; rows are asked for in order (0, 1, 2 …). Damaged data ends early, the rest stays 0.</summary>
        public abstract ReadOnlySpan<byte> Line(int y);

        public virtual void Dispose()
        {
        }
    }

    private sealed class ArrayRows(byte[] data, int rowBytes) : Rows
    {
        public override ReadOnlySpan<byte> Line(int y)
        {
            var start = (long)y * rowBytes;
            return start >= data.Length ? [] : data.AsSpan((int)start, (int)Math.Min(rowBytes, data.Length - start));
        }
    }

    private sealed class StreamedRows(ImageRows rows, int rowBytes) : Rows
    {
        private readonly byte[] _row = new byte[rowBytes];
        private int _next;

        public override ReadOnlySpan<byte> Line(int y)
        {
            while (_next <= y)
            {
                if (!rows.Read(_row)) Array.Clear(_row);
                _next++;
            }
            return _row;
        }

        public override void Dispose() => rows.Dispose();
    }

    private static Rows OpenRows(PdfDocument doc, ImageSource source, Setup s)
    {
        if (source.Stream is { } stream && ImageRows.Open(doc, stream, s.RowBytes) is { } rows) return new StreamedRows(rows, s.RowBytes);
        return new ArrayRows(Decoded(doc, source, s).Data, s.RowBytes);
    }

    /// <summary>Rows as stored (PNG packs samples the same way: from the most significant bit, rows padded to whole bytes).</summary>
    private static Action<int, Span<byte>> Packed(Rows rows, bool invert) => (y, row) =>
    {
        var line = rows.Line(y);
        line.CopyTo(row);
        // value → maximum − value is the same as flipping every bit, at any bit depth
        if (invert)
            for (var i = 0; i < row.Length; i++) row[i] = (byte)~row[i];
    };

    /// <summary>
    /// Rows with the colour channels of the image (bit depths below 8 spread to 8) and, if there is one, the opacity behind them: from
    /// the mask of the same size, or from the colour key (/Mask as an array, 8.9.6.4: raw values within all ranges are transparent).
    /// </summary>
    private static Action<int, Span<byte>> Channels(Setup s, Rows rows, AlphaRows? alpha, int depth, bool invert)
    {
        var n = s.Components;
        var withAlpha = alpha is not null || s.ColorKey is not null;
        var channels = withAlpha ? n + 1 : n;
        var max = (1 << s.Bpc) - 1;
        return (y, row) =>
        {
            var line = rows.Line(y);
            if (s.Bpc == depth && !withAlpha && !invert)
            {
                line.CopyTo(row);
                return;
            }
            // a 16-bit image keeps the opacity at 16 bits — a 16-bit mask with all its bits (before it went through 8 bits and lost its
            // lower byte, docs/FEHLERGEDAECHTNIS.md, No. 983)
            var a = alpha is null || depth == 16 ? default : alpha.Line(y);
            var wide = alpha is not null && depth == 16 ? alpha.Line16(y) : default;
            var outMax = (1 << depth) - 1;
            for (var x = 0; x < s.Width; x++)
            {
                var keyed = s.ColorKey is not null;
                // the opacity of the pixel, for unblending a /Matte (0 … 1)
                var opacity = s.Matte is null || alpha is null ? 1.0 : depth == 16 ? wide[x] / 65535.0 : a[x] / 255.0;
                for (var c = 0; c < n; c++)
                {
                    var v = Sample(line, x * n + c, s.Bpc);
                    if (keyed) keyed = v >= s.ColorKey![2 * c] && v <= s.ColorKey[2 * c + 1];
                    if (invert) v = max - v;
                    var value = s.Bpc == depth ? v : Spread(v, s.Bpc);
                    // the output value is the place of the component in its decode range times the maximum, and so is the matte (an
                    // inverted grey writes the component itself: its range 1 … 0 counts from 0)
                    if (opacity < 1) value = Unblend(value, MatteOut(s, c, outMax), opacity, outMax);
                    Put(row, x * channels + c, depth, value);
                }
                if (!withAlpha) continue;
                if (depth == 16)
                {
                    Put(row, x * channels + n, 16, s.ColorKey is not null ? keyed ? 0 : 65535 : wide[x]);
                    continue;
                }
                Put(row, x * channels + n, depth, s.ColorKey is not null ? keyed ? 0 : 255 : a[x]);
            }
        };
    }

    /// <summary>
    /// The rows and opacity of an image for an encoder that reads them twice (<see cref="PngEncoder.EncodeColour"/>): the first time those
    /// already open, later opened again (a streamed source is read once); what it opened is closed with it.
    /// </summary>
    private sealed class Reopener(PdfDocument doc, ImageSource source, Setup s, Rows rows, AlphaRows? alpha) : IDisposable
    {
        private readonly List<IDisposable> _opened = [];
        private bool _used;

        /// <summary>The rows already open.</summary>
        public Rows First => rows;

        /// <summary>The opacity already open, or null.</summary>
        public AlphaRows? FirstAlpha => alpha;

        public (Rows Rows, AlphaRows? Alpha) Next()
        {
            if (!_used)
            {
                _used = true;
                return (rows, alpha);
            }
            var r = OpenRows(doc, source, s);
            _opened.Add(r);
            AlphaRows? a = null;
            if (alpha is not null && OpenAlpha(doc, s) is { } fresh)
            {
                _opened.Add(fresh);
                a = fresh;
            }
            return (r, a);
        }

        public void Dispose()
        {
            foreach (var d in _opened) d.Dispose();
        }
    }

    private static void WriteIndexed(Setup s, Reopener again, Stream output)
    {
        var (palette, _, profile) = s.Palette!.Value;
        var (rows, alpha) = (again.First, again.FirstAlpha);
        if (alpha is null)
        {
            // colour key on indices: those entries become transparent in the palette (tRNS)
            byte[]? paletteAlpha = null;
            if (s.ColorKey is [var low, var high, ..])
            {
                paletteAlpha = new byte[palette.Length / 3];
                for (var i = 0; i < paletteAlpha.Length; i++) paletteAlpha[i] = i >= low && i <= high ? (byte)0 : (byte)255;
            }
            PngEncoder.Encode(s.Width, s.Height, s.Bpc, PngColorType.Palette, Packed(rows, false), output, palette, paletteAlpha, profile);
            return;
        }
        // a palette with an alpha channel per pixel: the colours with their opacity, as a palette image again if at most 256 of them
        // (RFC 2083, 9.6), otherwise RGBA; a /Matte unblended in the colours of the palette
        var matte = MatteRgb(s);
        PngEncoder.EncodeColour(s.Width, s.Height, true, () =>
        {
            var (r, opacity) = again.Next();
            return (y, row) =>
            {
                var line = r.Line(y);
                var a = opacity is null ? default : opacity.Line(y);
                for (var x = 0; x < s.Width; x++)
                {
                    var alphaX = opacity is null ? (byte)255 : a[x];
                    var i = Math.Min(Sample(line, x, s.Bpc), palette.Length / 3 - 1) * 3;
                    for (var c = 0; c < 3; c++)
                        row[x * 4 + c] = matte is null || alphaX == 255 ? palette[i + c] : (byte)Unblend(palette[i + c], matte[c], alphaX / 255.0, 255);
                    row[x * 4 + 3] = alphaX;
                }
            };
        }, output, profile, significantBits: alpha.Bits < 8 ? [8, 8, 8, (byte)alpha.Bits] : null);
    }

    /// <summary>Colours the file format cannot hold: converted row by row the way they are drawn, RGB with 8 bits.</summary>
    private static void WriteConverted(Setup s, Reopener again, Stream output)
    {
        var convert = PdfImageDecoder.RowToRgba(s.Space!, s.Bpc, s.Decode);
        var alpha = again.FirstAlpha;
        var withAlpha = alpha is not null || s.ColorKey is not null;
        // a /Matte unblended after the conversion to RGB, as when drawing (exact for linear conversions, docs/FEHLERGEDAECHTNIS.md, No. 562)
        var matte = alpha is null ? null : MatteRgb(s);
        var rgba = new byte[s.Width * 4];
        var raw = s.ColorKey is not null ? new ushort[s.Width * s.Components] : null;
        // at most 256 colours: a palette image (RFC 2083, 9.6)
        PngEncoder.EncodeColour(s.Width, s.Height, withAlpha, () =>
        {
            var (rows, opacity) = again.Next();
            return (y, row) =>
            {
                convert(rows.Line(y), s.Width, rgba, raw);
                var a = opacity is null ? default : opacity.Line(y);
                for (var x = 0; x < s.Width; x++)
                {
                    if (withAlpha)
                    {
                        rgba.AsSpan(x * 4, 3).CopyTo(row[(x * 4)..]);
                        row[x * 4 + 3] = opacity is not null ? a[x] : KeyTransparent(raw!, x, s) ? (byte)0 : (byte)255;
                        if (matte is not null && opacity is not null && a[x] < 255)
                            for (var c = 0; c < 3; c++) row[x * 4 + c] = (byte)Unblend(row[x * 4 + c], matte[c], a[x] / 255.0, 255);
                    }
                    else rgba.AsSpan(x * 4, 3).CopyTo(row[(x * 3)..]);
                }
            };
        }, output, significantBits: alpha is { Bits: < 8 } ? [8, 8, 8, (byte)alpha.Bits] : null);
    }

    /// <summary>
    /// The components of /Matte (ISO 32000-2, 11.6.5.3): as many finite numbers as the colour space has components (an indexed space: its
    /// base, whose palette colours are premultiplied), as the colour space has them (Lab and ICC ranges beyond 0 … 1 too, as when drawing);
    /// null otherwise (then the colours are taken as they are).
    /// </summary>
    private static double[]? MatteOf(PdfDictionary mask, ColorSpace space)
    {
        if (mask.Get(PdfName.Get("Matte")) is not PdfArray values) return null;
        var target = space.IndexedBase ?? space;
        if (values.Count != target.Components || target.Components is < 1 or > 32) return null;
        var components = new double[values.Count];
        for (var i = 0; i < components.Length; i++)
            if (values.GetNumber(i) is { } v && double.IsFinite(v)) components[i] = v;
            else return null;
        return components;
    }

    /// <summary>The matte of component <paramref name="c"/> as an output value 0 … <paramref name="max"/>: its place in the decode range.</summary>
    private static double MatteOut(Setup s, int c, int max)
    {
        var range = s.Decode ?? s.Space!.DefaultDecode(s.Bpc);
        double lo = Math.Min(range[2 * c], range[2 * c + 1]), hi = Math.Max(range[2 * c], range[2 * c + 1]);
        return hi > lo ? Math.Clamp((s.Matte![c] - lo) / (hi - lo), 0, 1) * max : 0;
    }

    /// <summary>The matte in RGB with 8 bits (for palettes and converted colours), or null.</summary>
    private static double[]? MatteRgb(Setup s)
    {
        if (s.Matte is null || s.Space is null) return null;
        var rgb = (s.Space.IndexedBase ?? s.Space).ToRgb(s.Matte);
        return [rgb.R, rgb.G, rgb.B];
    }

    /// <summary>
    /// c = m + (c′ − m) / α (ISO 32000-2, 11.6.5.3), rounded and bounded to 0 … <paramref name="max"/>; at opacity 0 the colour is undefined
    /// and written as 0 (RFC 2083, 9.4 Satz 13: black in a PNG; in a CMYK TIFF 0 is no ink).
    /// </summary>
    private static int Unblend(int premultiplied, double matte, double opacity, int max) =>
        opacity <= 0 ? 0 : (int)Math.Clamp(Math.Round(matte + (premultiplied - matte) / opacity), 0, max);

    /// <summary>
    /// sBIT for a grey or RGB image written at <paramref name="depth"/> (RFC 2083, 4.2.6 Satz 6: recommended when the data were scaled up from
    /// a smaller depth): the colour channels with the bits of the PDF image, unless a /Matte was unblended (then every bit counts); the alpha
    /// channel with the bits of its mask (a colour key gives full depth). Null when nothing was scaled up.
    /// </summary>
    private static byte[]? SignificantBits(Setup s, AlphaRows? alpha, int depth, int colours)
    {
        var colourBits = s.Matte is not null && alpha is not null ? depth : Math.Min(s.Bpc, depth);
        var withAlpha = alpha is not null || s.ColorKey is not null;
        var alphaBits = alpha is not null ? Math.Min(alpha.Bits, depth) : depth;
        if (colourBits == depth && alphaBits == depth) return null;
        var bits = new byte[colours + (withAlpha ? 1 : 0)];
        Array.Fill(bits, (byte)colourBits, 0, colours);
        if (withAlpha) bits[colours] = (byte)alphaBits;
        return bits;
    }

    /// <summary>The resolution of the image at its drawn size (pixels per inch on each axis), or null when the size is not known.</summary>
    private static (double X, double Y)? Resolution(Setup s, (double Width, double Height)? drawn) =>
        drawn is { Width: > 0, Height: > 0 } d ? (s.Width * 72 / d.Width, s.Height * 72 / d.Height) : null;

    /// <summary>The drawn size of an image from the matrix that maps its unit square onto the page: the lengths of its two axes in points.</summary>
    public static (double Width, double Height) DrawnSize(Matrix imageToPage) =>
        (Math.Sqrt(imageToPage.A * imageToPage.A + imageToPage.B * imageToPage.B), Math.Sqrt(imageToPage.C * imageToPage.C + imageToPage.D * imageToPage.D));

    private static bool KeyTransparent(ushort[] raw, int x, Setup s)
    {
        for (var c = 0; c < s.Components; c++)
        {
            var v = raw[x * s.Components + c];
            if (v < s.ColorKey![2 * c] || v > s.ColorKey[2 * c + 1]) return false;
        }
        return true;
    }

    // ------------------------------------------------------------ masks

    /// <summary>The opacity of a mask row by row, 0 … 255 per pixel.</summary>
    private sealed class AlphaRows(Rows? rows, byte[]? whole, int width, int bpc, bool stencil, bool inverted) : IDisposable
    {
        private readonly byte[] _line = new byte[width];

        /// <summary>The bits of the opacity as the mask has them: 1 for a stencil, 8 for a mask decoded as drawn.</summary>
        public int Bits => whole is not null ? 8 : stencil ? 1 : bpc;

        public ReadOnlySpan<byte> Line(int y)
        {
            if (whole is not null) return whole.AsSpan(y * width, width);
            var line = rows!.Line(y);
            var max = (1 << bpc) - 1;
            for (var x = 0; x < width; x++)
            {
                var v = Sample(line, x, bpc);
                if (stencil)
                    // image mask: visible where the stencil would paint (0, or 1 with /Decode [1 0]), 8.9.6.3
                    _line[x] = (v == 0) != inverted ? (byte)255 : (byte)0;
                else
                    _line[x] = (byte)((inverted ? max - v : v) * 255 / max);
            }
            return _line;
        }

        private ushort[]? _wide;

        /// <summary>The opacity of a row at 16 bits: a mask of 16 bits with all of them, smaller depths spread by repeating their bits.</summary>
        public ReadOnlySpan<ushort> Line16(int y)
        {
            _wide ??= new ushort[width];
            if (whole is not null)
            {
                for (var x = 0; x < width; x++) _wide[x] = (ushort)(whole[y * width + x] * 257);
                return _wide;
            }
            var line = rows!.Line(y);
            var max = (1 << bpc) - 1;
            for (var x = 0; x < width; x++)
            {
                var v = Sample(line, x, bpc);
                _wide[x] = stencil ? (v == 0) != inverted ? (ushort)65535 : (ushort)0 : (ushort)((long)(inverted ? max - v : v) * 65535 / max);
            }
            return _wide;
        }

        public void Dispose() => rows?.Dispose();
    }

    private static AlphaRows? OpenAlpha(PdfDocument doc, Setup s)
    {
        var mask = s.Mask!;
        var d = mask.Dictionary;
        var stencil = d.GetBoolean(PdfName.Get("ImageMask")) ?? false;
        var bpc = stencil ? 1 : (int)(Get(d, "BitsPerComponent", "BPC").AsNumber() ?? 8);
        var decode = PdfFunction.Numbers(Get(d, "Decode", "D") as PdfArray);
        var inverted = decode is { Length: >= 2 } && decode[0] > decode[1];
        var chain = StreamFilters.Chain(d);
        var imageFilter = chain.Count > 0 && chain[^1].Name is "DCTDecode" or "JPXDecode" or "JBIG2Decode";
        if (!imageFilter && bpc is 1 or 2 or 4 or 8 or 16)
        {
            var rowBytes = (int)(((long)s.Width * bpc + 7) / 8);
            Rows rows = ImageRows.Open(doc, mask, rowBytes) is { } streamed ? new StreamedRows(streamed, rowBytes) : new ArrayRows(doc.DecodeStream(mask).Data, rowBytes);
            return new AlphaRows(rows, null, s.Width, bpc, stencil, inverted);
        }
        // JPEG, JPEG 2000, JBIG2 masks: decoded as drawn (up to 16 megapixels; a larger one stays beside, see Analyse)
        return PdfImageDecoder.Alpha(mask, doc) is { } a && a.Width == s.Width && a.Height == s.Height ? new AlphaRows(null, a.Alpha, s.Width, 8, false, false) : null;
    }

    /// <summary>The opacity as a greyscale PNG of its own (white = opaque), from the mask at its own size.</summary>
    private static void WriteMask(PdfDocument doc, PdfStream mask, Func<Stream> open)
    {
        var d = mask.Dictionary;
        var width = (int)(Get(d, "Width", "W").AsNumber() ?? 0);
        var height = (int)(Get(d, "Height", "H").AsNumber() ?? 0);
        var chain = StreamFilters.Chain(d);
        var stencil = d.GetBoolean(PdfName.Get("ImageMask")) ?? false;
        var bpc = stencil ? 1 : (int)(Get(d, "BitsPerComponent", "BPC").AsNumber() ?? 8);
        if (width > 0 && height > 0 && width <= PdfImageDecoder.MaxSide && height <= PdfImageDecoder.MaxSide && bpc is 1 or 2 or 4 or 8 or 16
            && !(chain.Count > 0 && chain[^1].Name is "DCTDecode" or "JPXDecode" or "JBIG2Decode"))
        {
            var decode = PdfFunction.Numbers(Get(d, "Decode", "D") as PdfArray);
            var setup = new Setup(Kind.Gray, width, height, bpc, 1, (int)(((long)width * bpc + 7) / 8), d, ColorSpace.DeviceGray, decode, false, null, null, null,
                null, false, ".png");
            using var rows = OpenRows(doc, new ImageSource(mask, null, null, false, 0), setup);
            var inverted = decode is { Length: >= 2 } && decode[0] > decode[1];
            if (bpc < 8)
            {
                // at its own depth: PNG spreads grey the same way (v × 255 / (2^bits − 1)), and a mask of a large image stays an eighth
                // of the data; an image mask is visible where its bit is 0 (8.9.6.3), so its bits are flipped unless /Decode flips them
                PngEncoder.Encode(width, height, bpc, PngColorType.Gray, Packed(rows, stencil != inverted), open());
                return;
            }
            using var alpha = new AlphaRows(rows, null, width, bpc, stencil, inverted);
            PngEncoder.Encode(width, height, 8, PngColorType.Gray, (y, row) => alpha.Line(y).CopyTo(row), open());
            return;
        }
        if (PdfImageDecoder.Alpha(mask, doc) is not { } a) return;
        PngEncoder.Encode(a.Width, a.Height, 8, PngColorType.Gray, (y, row) => a.Alpha.AsSpan(y * a.Width, a.Width).CopyTo(row), open());
    }

    // ------------------------------------------------------------ samples

    private static int Sample(ReadOnlySpan<byte> line, int index, int bpc)
    {
        switch (bpc)
        {
            case 8:
                return index < line.Length ? line[index] : 0;
            case 16:
                return index * 2 + 1 < line.Length ? line[index * 2] << 8 | line[index * 2 + 1] : 0;
            default:
                var bit = (long)index * bpc;
                var b = (int)(bit >> 3);
                return b < line.Length ? line[b] >> (8 - bpc - (int)(bit & 7)) & ((1 << bpc) - 1) : 0;
        }
    }

    private static int Spread(int v, int bpc) => bpc == 8 ? v : v * 255 / ((1 << bpc) - 1);

    private static void Put(Span<byte> row, int index, int depth, int v)
    {
        if (depth == 8)
        {
            row[index] = (byte)v;
            return;
        }
        row[index * 2] = (byte)(v >> 8);
        row[index * 2 + 1] = (byte)v;
    }

    // ------------------------------------------------------------ colour spaces

    // the same rule as the display (a key into the void counts as absent; docs/FEHLERGEDAECHTNIS.md, No. 948)
    private static PdfObject Get(PdfDictionary d, string full, string abbr) => PdfImageDecoder.Get(d, full, abbr);

    /// <summary>A name from the resources (/ColorSpace &lt;&lt; /CS0 … &gt;&gt;) replaced by what it stands for.</summary>
    private static PdfObject Resolve(PdfObject space, PdfDictionary? resources) =>
        space is PdfName named && named.Value is not ("DeviceGray" or "G" or "DeviceRGB" or "RGB" or "DeviceCMYK" or "CMYK" or "Pattern")
        && resources?.GetDictionary(ColorSpaceKey)?.Get(named) is not PdfNull and { } resolved
            ? resolved
            : space;

    private static string? Family(PdfObject space) => (space switch
    {
        PdfName n => n.Value,
        PdfArray { Count: > 0 } a when a.Get(0) is PdfName n => n.Value,
        _ => null,
    }) switch
    {
        "G" => "DeviceGray",
        "RGB" => "DeviceRGB",
        "CMYK" => "DeviceCMYK",
        "I" => "Indexed",
        var f => f,
    };

    /// <summary>
    /// The profile that says what the raw values mean: the ICC profile of the PDF, for CalGray/CalRGB one built from their parameters;
    /// null for device colour spaces and the rest.
    /// </summary>
    private static byte[]? ProfileOf(PdfDocument doc, PdfObject space, string? family) => family switch
    {
        "ICCBased" => Profile(doc, space),
        "CalGray" => Icc.CalibratedProfile.Gray((space as PdfArray)?.Get(1).AsDictionary()),
        "CalRGB" => Icc.CalibratedProfile.Rgb((space as PdfArray)?.Get(1).AsDictionary()),
        _ => null,
    };

    private static string? ProfileSpace(byte[]? profile) => profile is { Length: >= 20 } ? System.Text.Encoding.ASCII.GetString(profile, 16, 4) : null;

    private static byte[]? Profile(PdfDocument doc, PdfObject space)
    {
        if (space is not PdfArray a || a.Get(1) is not PdfStream stream || stream.DataLength > MaxIccStream) return null;
        try
        {
            return doc.GetStreamData(stream);
        }
        catch (PdfException)
        {
            return null;
        }
    }

    /// <summary>The CMYK profile for DeviceCMYK: that of the output intent, otherwise the one the program draws with.</summary>
    private static byte[]? CmykProfile(PdfDocument doc)
    {
        try
        {
            if (doc.Catalog?.GetArray(PdfName.Get("OutputIntents")) is { } intents)
                for (var i = 0; i < Math.Min(intents.Count, 16); i++)
                    if (intents.Get(i).AsDictionary()?.Get(PdfName.Get("DestOutputProfile")) is PdfStream stream
                        && stream.Dictionary.GetInteger(PdfName.N) is null or 4 && stream.DataLength <= MaxIccStream)
                        return doc.GetStreamData(stream);
        }
        catch (PdfException)
        {
        }
        return Icc.CmykProfile.Bytes();
    }

    /// <summary>
    /// The palette as RGB triples with the profile that describes them: taken over unchanged from an RGB base (DeviceRGB, CalRGB, ICC
    /// RGB — the latter two with their profile) or DeviceGray; any other base (CMYK, Lab, spot colours, calibrated grey) converted the way
    /// it is drawn. Null if the palette has more than 256 entries.
    /// </summary>
    private static (byte[] Rgb, bool Converted, byte[]? Profile)? Palette(PdfDocument doc, PdfArray array, ColorSpace space, PdfDictionary? resources)
    {
        if (array.Count < 4) return null;
        var hival = (int)(array.GetNumber(2) ?? 0);
        if (hival is < 0 or > 255) return null;
        var baseObj = Resolve(array.Get(1), resources);
        var baseFamily = Family(baseObj);
        var profile = ProfileOf(doc, baseObj, baseFamily);
        var baseN = baseFamily switch
        {
            "DeviceGray" => 1,
            "DeviceRGB" or "CalRGB" => 3,
            "ICCBased" when ProfileSpace(profile) == "RGB " => 3,
            _ => 0,
        };
        if (baseN != 3) profile = null;
        var lookup = array.Get(3) switch
        {
            PdfString s => s.Bytes,
            PdfStream st => doc.GetStreamData(st),
            _ => [],
        };
        var rgb = new byte[(hival + 1) * 3];
        for (var i = 0; i <= hival; i++)
        {
            if (baseN is 1 or 3)
            {
                for (var c = 0; c < 3; c++)
                {
                    var at = i * baseN + (baseN == 1 ? 0 : c);
                    rgb[i * 3 + c] = at < lookup.Length ? lookup[at] : (byte)0;
                }
                continue;
            }
            var color = space.ToRgb([i]);
            (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]) = (color.R, color.G, color.B);
        }
        return (rgb, baseN == 0, profile);
    }
}
