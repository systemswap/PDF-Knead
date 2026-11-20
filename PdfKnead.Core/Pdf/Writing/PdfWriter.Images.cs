using System.IO.Compression;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Filters.Jbig2;
using PdfKnead.Core.Pdf.Filters.Jpeg;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        private static readonly PdfName ImageName = PdfName.Get("Image"), Width = PdfName.Get("Width"), Height = PdfName.Get("Height"),
            Bpc = PdfName.Get("BitsPerComponent"), ColorSpaceKey = PdfName.Get("ColorSpace"), ImageMask = PdfName.Get("ImageMask"),
            DecodeKey = PdfName.Get("Decode"), MaskKey = PdfName.Get("Mask"), DeviceGray = PdfName.Get("DeviceGray"),
            Dct = PdfName.Get("DCTDecode"), Ccitt = PdfName.Get("CCITTFaxDecode"), Jbig2Name = PdfName.Get("JBIG2Decode"), SMaskKey = PdfName.Get("SMask"), Matte = PdfName.Get("Matte"),
            FormName = PdfName.Get("Form");

        /// <summary>
        /// Effective resolution (pixels per inch) of each source image at the place where it is drawn largest (also in forms,
        /// annotations, tiling patterns and soft masks), because that is where it needs the most pixels. Images that do not
        /// occur this way are missing; they are not downsampled.
        /// </summary>
        private Dictionary<(PdfDocument, int), double>? _imagePpi;

        /// <summary>Smallest resolution per content (checksum): identical images are merged into one, which must suffice for every use.</summary>
        private Dictionary<string, double>? _ppiByContent;

        private Dictionary<(PdfDocument, int), double> ImagePpi()
        {
            if (_imagePpi is not null) return _imagePpi;
            // With the cache: the display lists of all pages only in the first run (book with 352 pages: 0.5 GB allocated per run)
            if (CacheMatches && Cache!.ImagePpi is { } known && Cache.MaskImages is { } knownMasks)
            {
                (_ppiByContent, _maskImages) = (Cache.PpiByContent, knownMasks);
                return _imagePpi = known;
            }
            var result = new Dictionary<(PdfDocument, int), double>();
            var masks = new HashSet<(PdfDocument, int)>();
            foreach (var item in _pages)
            {
                _cancel.ThrowIfCancellationRequested();
                Graphics.DisplayList list;
                try
                {
                    list = PageInterpreter.Build(item.Source, item.Page, _cancel);
                }
                catch (Exception e) when (e is PdfException or InvalidDataException)
                {
                    continue;
                }
                var budget = 10_000;
                MeasureImages(list, Graphics.Matrix.Identity, item.Source, result, masks, 0, ref budget);
            }
            _ppiByContent = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var ((doc, number), ppi) in result)
                if (_sources.TryGetValue(doc, out var source) && ContentKey(source, number, 0, []) is { } key)
                    _ppiByContent[key] = _ppiByContent.TryGetValue(key, out var shared) ? Math.Min(shared, ppi) : ppi;
            _maskImages = masks;
            if (CacheMatches) (Cache!.ImagePpi, Cache.PpiByContent, Cache.MaskImages) = (result, _ppiByContent, masks);
            return _imagePpi = result;
        }

        /// <summary>Images of a display list (space → page: <paramref name="toPage"/>), also in tiling patterns and soft masks.</summary>
        private static void MeasureImages(Graphics.DisplayList list, Graphics.Matrix toPage, PdfDocument doc, Dictionary<(PdfDocument, int), double> result,
            HashSet<(PdfDocument, int)> masks, int depth, ref int budget)
        {
            foreach (var entry in list.Items.Span)
            {
                if (entry.Kind != Graphics.DisplayItemKind.Image || entry.Image < 0 || entry.Image >= list.Images.Length) continue;
                if (list.Images[entry.Image].Stream is not { Id: { } id } stream) continue;
                foreach (var maskKey in new[] { SMaskKey, MaskKey })
                    if (stream.Dictionary.GetRaw(maskKey) is PdfReference maskRef) masks.Add((doc, maskRef.Id.Number));
                var m = list.Matrices[entry.Matrix] * toPage;
                // Unit square → page: width and height of the drawn image in points
                var widthPt = Math.Sqrt(m.A * m.A + m.B * m.B);
                var heightPt = Math.Sqrt(m.C * m.C + m.D * m.D);
                var w = stream.Dictionary.GetInteger(Width) ?? 0;
                var h = stream.Dictionary.GetInteger(Height) ?? 0;
                if (widthPt < 0.01 || heightPt < 0.01 || w <= 0 || h <= 0) continue;
                // The smaller of the two directions, so that an image is never downsampled too much
                var ppi = Math.Min(w / (widthPt / 72), h / (heightPt / 72));
                var key = (doc, id.Number);
                result[key] = result.TryGetValue(key, out var known) ? Math.Min(known, ppi) : ppi;
            }
            // Pattern cells and masks are separate lists in their own space (relative to the enclosing list)
            if (depth >= 8) return;
            foreach (var fill in list.Shadings)
                if (fill is Graphics.TilingFill tiling && --budget >= 0)
                    MeasureImages(tiling.Cell, tiling.ToPage * toPage, doc, result, masks, depth + 1, ref budget);
            foreach (var mask in list.Masks)
                if (--budget >= 0) MeasureImages(mask.Content, mask.ToPage * toPage, doc, result, masks, depth + 1, ref budget);
        }

        /// <summary>Resolution the image must keep: the smallest over all identical images; 0 = unknown (do not downsample).</summary>
        private double PpiOf(Source source, int number)
        {
            var own = ImagePpi().GetValueOrDefault((source.Doc, number));
            return own > 0 && ContentKey(source, number, 0, []) is { } key && _ppiByContent!.TryGetValue(key, out var shared) ? Math.Min(own, shared) : own;
        }

        /// <summary>
        /// Merging identical images during lossy compression: an image without a measurement (it is not downsampled) is never
        /// merged with a measured one; otherwise the downsampled one would appear in its place.
        /// </summary>
        private string? ImageKeySuffix(Source source, int old)
        {
            if (_options.Compression is not { Lossy: true }) return null;
            var obj = source.Overrides.TryGetValue(old, out var replaced) ? replaced : source.Doc.GetObject(old);
            if (obj is not PdfStream s || s.Dictionary.GetName(PdfName.Subtype) != ImageName) return null;
            return ImagePpi().ContainsKey((source.Doc, old)) ? null : "|ungemessen";
        }

        /// <summary>
        /// Repacks an image when compressing: pure black-and-white images as 1-bit images with the selected codec (CCITT G4 or
        /// JBIG2, lossless); with lossy presets, images above 1.5 times the target resolution are downsampled, photos become
        /// JPEG, graphics stay lossless. Null = stays as it is. Only if the result is smaller.
        /// </summary>
        private (byte[] Data, PdfDictionary Dict)? RewriteImage(PdfStream stream, Source source, PdfDictionary dict, ReadOnlySpan<byte> raw)
        {
            if (_options.Compression is not { } compression || stream.Id is null || dict.GetName(PdfName.Subtype) != ImageName) return null;
            // With the cache: the same image with the same settings only once (switching back and forth in the window)
            if (!CacheMatches || !Cacheable(source, stream, out var number)) return Rewrite(stream, source, dict, raw, null);
            // Without downsampling, JPEG quality and palette have no effect: they are left out of the key, otherwise every switch would recompute everything
            var lossy = compression.Lossy;
            var key = (source.Doc, number, new CompressionCache.ImageSettings(compression.ImageDpi ?? 0, lossy ? compression.JpegQuality : 0,
                compression.BilevelScans, compression.BilevelCodec, compression.PackKind, lossy ? compression.Graphics : GraphicsMethod.Flate,
                lossy ? compression.PaletteColors : 0, lossy ? compression.PaletteDither : 0, lossy ? compression.PaletteQuality : 0, MrcKey(compression)));
            if (Cache!.Images.TryGetValue(key, out var known)) return known is { } k ? (k.Data, Copy(k.Dict)) : null;
            var result = Rewrite(stream, source, dict, raw, (source.Doc, number, compression.Lossy, compression.BilevelCodec));
            // An MRC form refers to objects of this run; it comes from its own cache (layers), not from here
            if (result is not { Dict: var d } || d.GetName(PdfName.Subtype) != FormName) Cache.AddImage(key, result is { } r ? (r.Data, Copy(r.Dict)) : null);
            return result;
        }

        private static CompressionCache.MrcSettings MrcKey(CompressionSettings c) => c.Scans == ScanMethod.Mrc && c.Lossy
            ? new(c.MrcTextDpi, c.MrcBackgroundDpi, c.MrcBackgroundQuality, c.MrcSensitivity, c.MrcColors, c.BilevelCodec, c.MrcFlattenPaper)
            : default;

        // ------------------------------------------------------------ MRC

        /// <summary>Only from this size on can an image be a text scan (A5 at 200 dpi has 1.9 megapixels).</summary>
        private const long MrcMinPixels = 1_500_000;

        /// <summary>Largest width and height that is repacked (some readers read 1-bit images only up to 65,535 columns).</summary>
        private const int MaxSide = 65_535;

        /// <summary>MRC only up to this size (A1 at 300 dpi has 70 megapixels): the separation needs about 11 bytes per pixel.</summary>
        private const long MrcMaxPixels = 80_000_000;

        /// <summary>
        /// Images that serve a drawn image as /SMask or /Mask: they stay images. Collected while measuring the resolutions
        /// (<see cref="ImagePpi"/>), so that not every object of the file has to be loaded for this (and kept in the document's memory).
        /// </summary>
        private HashSet<(PdfDocument, int)>? _maskImages;

        private bool UsedAsMask(Source source, int number)
        {
            ImagePpi();
            return _maskImages!.Contains((source.Doc, number));
        }

        /// <summary>
        /// Text scan as layers: text per colour as a 1-bit mask (with the selected codec, G4 or JBIG2), the paper at low resolution
        /// as JPEG, as a form XObject in place of the image (the page content, including invisible OCR text, stays). Null if it is
        /// not a text scan or does not become smaller.
        /// </summary>
        private (byte[] Data, PdfDictionary Dict)? Mrc(Source source, int number, PdfDictionary dict, (byte[] Pixels, int Width, int Height, int Stride) image,
            CompressionSettings compression, int rawLength, bool cacheable)
        {
            var parts = MrcParts(source, number, dict, image, compression);
            if (cacheable && CacheMatches) Cache!.AddMrc((source.Doc, number, MrcKey(compression)), parts);
            return MrcForm(parts, source, dict, rawLength);
        }

        /// <summary>The form built from the layers (also from the cache); null if there are none or they are not smaller.</summary>
        private (byte[] Data, PdfDictionary Dict)? MrcForm(CompressionCache.MrcParts? parts, Source source, PdfDictionary dict, int rawLength)
        {
            if (parts is null || parts.Bytes >= rawLength) return null;
            // Written via the source: the background can refer to the ICC profile of the original
            var xobjects = new PdfDictionary(parts.Images.Count);
            foreach (var (name, imageDict, data) in parts.Images) xobjects.Set(PdfName.Get(name), Sourced(new PdfStream(Copy(imageDict), data), source));
            var resources = new PdfDictionary();
            resources.Set(PdfName.Get("XObject"), xobjects);
            if (!DeviceSpace(dict))
            {
                var spaces = new PdfDictionary();
                spaces.Set(PdfName.Get("Cs0"), dict.GetRaw(ColorSpaceKey)!);
                resources.Set(PdfName.Get("ColorSpace"), spaces);
            }
            var form = new PdfDictionary();
            form.Set(PdfName.Type, PdfName.Get("XObject"));
            form.Set(PdfName.Subtype, FormName);
            form.Set(PdfName.Get("BBox"), new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(1), PdfInteger.Of(1)]));
            form.Set(PdfName.Resources, resources);
            // Layer, place in the structure (tagged PDFs), metadata and measurements (GeoPDF) of the image still apply
            foreach (var key in new[] { "OC", "StructParent", "Metadata", "Measure", "PtData" })
                if (dict.GetRaw(PdfName.Get(key)) is { } value) form.Set(PdfName.Get(key), value);
            return (parts.Content, form);
        }

        private CompressionCache.MrcParts? MrcParts(Source source, int number, PdfDictionary dict, (byte[] Pixels, int Width, int Height, int Stride) image,
            CompressionSettings c)
        {
            if (_options.ImageCodec is not { } codec) return null;
            var (pixels, width, height, stride) = image;
            // Text at most at the set resolution (a 600 dpi scan is downsampled for the masks)
            var ppi = PpiOf(source, number);
            if (ppi <= 0) ppi = 300;
            if (ppi > c.MrcTextDpi * 1.25)
            {
                var f = c.MrcTextDpi / ppi;
                var (w, h) = (Math.Max(64, (int)Math.Round(width * f)), Math.Max(64, (int)Math.Round(height * f)));
                pixels = Average(pixels, width, height, stride, w, h, _cancel);
                (width, height, ppi) = (w, h, ppi * w / image.Width);
            }
            // Text masks at double resolution if the set resolution allows it (300 dpi scan, text at 600 dpi): smooth edges
            var maskScale = ppi * 2 <= c.MrcTextDpi * 1.25 && Math.Max(width, height) * 2 <= MaxSide ? 2 : 1;
            // Background: low resolution, as JPEG (grey if the scan is grey)
            var options = new ScanOptions(c.MrcSensitivity / 100.0, c.MrcColors, Math.Min(1, c.MrcBackgroundDpi / ppi), maskScale, ppi, c.MrcFlattenPaper);
            if (ScanSegmenter.Split(pixels, width, height, stride, options, _cancel) is not { } layers) return null;
            _cancel.ThrowIfCancellationRequested();

            var images = new List<(string, PdfDictionary, byte[])>();
            var content = new System.Text.StringBuilder();
            // Colour space: Device* as computed; ICC/Cal* of the original stay (DeviceRGB instead would shift the colours). The values
            // are those of the original anyway; a three-channel space then also needs a three-channel background.
            var device = DeviceSpace(dict);
            var components = device ? (layers.Gray ? 1 : 3) : Components(dict.Get(ColorSpaceKey));
            var (bw, bh) = (layers.BackgroundWidth, layers.BackgroundHeight);
            var background = components == 3 && layers.Gray ? Rgb(layers.Background) : layers.Background;
            if (codec.EncodeJpeg(bw, bh, components, background, c.MrcBackgroundQuality) is not { } jpeg) return null;
            var bgDict = new PdfDictionary();
            bgDict.Set(PdfName.Type, PdfName.Get("XObject"));
            bgDict.Set(PdfName.Subtype, ImageName);
            bgDict.Set(Width, PdfInteger.Of(bw));
            bgDict.Set(Height, PdfInteger.Of(bh));
            bgDict.Set(ColorSpaceKey, !device ? dict.GetRaw(ColorSpaceKey)! : components == 1 ? DeviceGray : PdfName.Get("DeviceRGB"));
            bgDict.Set(Bpc, PdfInteger.Of(8));
            bgDict.Set(PdfName.Filter, Dct);
            images.Add(("Bg", bgDict, jpeg));
            content.Append("/Bg Do\n");
            // Text: one mask per colour at full (or double) resolution (0 paints)
            var (mw, mh) = (width * layers.MaskScale, height * layers.MaskScale);
            var rowBytes = (mw + 7) / 8;
            var encoded = new byte[]?[layers.Masks.Count];
            var maskDicts = new PdfDictionary[layers.Masks.Count];
            Parallel.For(0, encoded.Length, new ParallelOptions { CancellationToken = _cancel }, k =>
            {
                var bits = layers.Masks[k].Bits;
                if (bits.AsSpan().IndexOfAnyExcept((byte)0xFF) >= 0) (encoded[k], maskDicts[k]) = OneBitEncoded(bits, mw, mh, rowBytes, new PdfDictionary(), gray: false);
            });
            for (var k = 0; k < layers.Masks.Count; k++)
            {
                var mask = layers.Masks[k];
                if (encoded[k] is not { } data) continue;
                var maskDict = maskDicts[k];
                maskDict.Set(PdfName.Type, PdfName.Get("XObject"));
                maskDict.Set(PdfName.Subtype, ImageName);
                maskDict.Set(Width, PdfInteger.Of(mw));
                maskDict.Set(Height, PdfInteger.Of(mh));
                maskDict.Set(ImageMask, PdfBoolean.True);
                maskDict.Set(Bpc, PdfInteger.Of(1));
                images.Add(($"M{k}", maskDict, data));
                var values = components == 1
                    ? FormattableString.Invariant($"{mask.R / 255.0:0.###}")
                    : FormattableString.Invariant($"{mask.R / 255.0:0.###} {mask.G / 255.0:0.###} {mask.B / 255.0:0.###}");
                content.Append(!device ? $"/Cs0 cs {values} sc /M{k} Do\n" : components == 1 ? $"{values} g /M{k} Do\n" : $"{values} rg /M{k} Do\n");
            }
            return new CompressionCache.MrcParts(System.Text.Encoding.ASCII.GetBytes(content.ToString()), images);
        }

        /// <summary>Device colour space (a name)? Otherwise ICC or Cal*, which MRC keeps.</summary>
        private static bool DeviceSpace(PdfDictionary dict) => dict.Get(ColorSpaceKey) is PdfName;

        /// <summary>Grey (1 byte per pixel) as RGB.</summary>
        private static byte[] Rgb(byte[] gray)
        {
            var rgb = new byte[gray.Length * 3];
            for (var i = 0; i < gray.Length; i++) rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = gray[i];
            return rgb;
        }

        /// <param name="bilevelKey">Key for the black-and-white detection in the cache (it does not depend on resolution and quality).</param>
        private (byte[] Data, PdfDictionary Dict)? Rewrite(PdfStream stream, Source source, PdfDictionary dict, ReadOnlySpan<byte> raw,
            (PdfDocument, int, bool, BilevelMethod)? bilevelKey)
        {
            var compression = _options.Compression!;
            var id = stream.Id!.Value;
            // Above 65,535 pixels per side the image stays as it is (capping it would wrap the rows wrongly: data loss with long drawings)
            var (w0, h0) = (dict.GetInteger(Width) ?? 0, dict.GetInteger(Height) ?? 0);
            if (w0 is <= 0 or > MaxSide || h0 is <= 0 or > MaxSide || w0 * h0 > 200_000_000) return null;
            var (width, height) = ((int)w0, (int)h0);
            var isMask = dict.GetBoolean(ImageMask) ?? false;
            var bpc = isMask ? 1 : (int)(dict.GetInteger(Bpc) ?? 8);
            var chain = StreamFilters.Chain(dict);
            // Existing G4 is re-encoded only if JBIG2 is selected (the same bits, about one third smaller). Existing JBIG2 stays: re-encoding
            // it saved 40 KB over the public test collection for 9.5 s more writing time
            var reencode = compression.BilevelCodec == BilevelMethod.Jbig2 && bpc == 1;
            if (chain.Any(f => f.Name is "JPXDecode" or "JBIG2Decode" or "Crypt" || (f.Name == "CCITTFaxDecode" && !reencode))) return null;
            try
            {
                if (bpc == 1) return isMask || OneComponent(dict.Get(ColorSpaceKey)) ? OneBit(dict, raw, width, height) : null;
                if (bpc != 8 || isMask || dict.ContainsKey(DecodeKey) || dict.Get(MaskKey) is PdfArray) return null;
                var components = Components(dict.Get(ColorSpaceKey));
                if (components is not (1 or 3)) return null;
                var wasJpeg = chain.Count > 0 && chain[^1].Name == "DCTDecode";
                // If the JPEG header states a different size than the PDF (placeholder height of some scanners, length only in the DNL
                // marker): leave it unchanged without decoding; libjpeg would otherwise work at the header size (test file
                // issue10880: 10,248 × 65,280, 4.6 s and 860 MB). See docs/FEHLERGEDAECHTNIS.md, No. 72.
                if (wasJpeg && chain.Count == 1 && JpegSize(raw) is { } header && header != (width, height)) return null;

                // Decide first, then decode (otherwise every photo would be fully decoded even if nothing is done with it)
                double scale = 1;
                if (compression.ImageDpi is { } dpi)
                {
                    var ppi = PpiOf(source, id.Number);
                    if (ppi > dpi * CompressionSettings.DownsampleThreshold) scale = dpi / ppi;
                    // Soft mask with /Matte (premultiplied): image and mask must keep the same size (ISO 32000-2, 11.6.5.2, Table 144)
                    if (scale < 1 && dict.Get(SMaskKey) is PdfStream smask && smask.Dictionary.ContainsKey(Matte)) scale = 1;
                }
                // Black-and-white check: lossless needs exactly two values, which a JPEG never delivers
                var bilevel = compression.BilevelScans && (compression.Lossy || !wasJpeg);
                if (!bilevel) bilevelKey = null;
                if (bilevelKey is { } known && Cache!.Bilevel.TryGetValue(known, out var cached))
                {
                    // Already checked (in an earlier run): 1-bit result, or certainly not black and white; then do not fully decode again
                    if (cached is { } g) return (g.Data, Copy(g.Dict));
                    (bilevel, bilevelKey) = (false, null);
                }
                // Colour JPEG: check on a one-eighth version (almost free with libjpeg) whether it can be black and white at all
                if (bilevel && wasJpeg && components == 3 && Colored(raw, dict))
                {
                    bilevel = false;
                    if (bilevelKey is { } colored) Cache!.Bilevel[colored] = null;
                }
                // Text scan as layers: large images without a mask that do not serve another image as a mask
                var mrc = compression.Lossy && compression.Scans == ScanMethod.Mrc && (long)width * height is >= MrcMinPixels and <= MrcMaxPixels &&
                          !dict.ContainsKey(SMaskKey) && !dict.ContainsKey(MaskKey) && !UsedAsMask(source, id.Number);
                // Already known (earlier run with the same MRC settings): form without decoding, or certainly not a text scan
                var mrcCacheable = Cacheable(source, stream, out _);
                var mrcKey = (source.Doc, id.Number, MrcKey(compression));
                if (mrc && mrcCacheable && CacheMatches && Cache!.Mrc.TryGetValue(mrcKey, out var knownParts))
                {
                    if (MrcForm(knownParts, source, dict, raw.Length) is { } knownForm) return knownForm;
                    mrc = false;
                }
                // Check a JPEG on the one-eighth version first (almost free with libjpeg): uniform light paper? Otherwise (usually a photo)
                // do not decode it fully; without this check, every large JPEG, photos included, would be fully decoded and separated
                if (mrc && wasJpeg && !MayBeScan(raw, dict))
                {
                    mrc = false;
                    if (mrcCacheable && CacheMatches) Cache!.AddMrc(mrcKey, null);
                }
                // Nothing to do, the JPEG stays; everything else can become smaller losslessly (predictor, exact palette)
                if (!bilevel && !mrc && wasJpeg && (compression.ImageDpi is null || scale >= 1)) return null;
                var (w, h) = scale < 1 ? (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale))) : (width, height);

                // Decoding: fully if black and white is possible (the bits need every pixel); a JPEG that is downsampled is decoded
                // directly at the smallest libjpeg scale that still has at least the target size
                var full = bilevel || !wasJpeg || mrc;
                var decoded = Samples(dict, chain, raw, width, height, components, full ? long.MaxValue : JpegPixels(width, height, w, h));
                if (decoded is not { } image) return null;
                _cancel.ThrowIfCancellationRequested();
                if (bilevel && image.Width == width)
                {
                    // Really black and white (scan): 1 bit, JBIG2 or G4; lossless with exactly two values, otherwise only when lossy is allowed
                    (byte[] Data, PdfDictionary Dict)? g4 = null;
                    if (Bilevel(image.Pixels, image.Stride, compression.Lossy, width, height) is { } bits &&
                        OneBitEncoded(bits, width, height, (width + 7) / 8, dict, gray: true) is var encoded && encoded.Data.Length < raw.Length)
                        g4 = encoded;
                    if (bilevelKey is { } k && Cache!.Charge(g4?.Data.Length ?? 0)) Cache.Bilevel[k] = g4 is { } c ? (c.Data, Copy(c.Dict)) : null;
                    if (g4 is not null) return g4;
                }
                if (mrc && image.Width == width && image.Height == height && Mrc(source, id.Number, dict, image, compression, raw.Length, mrcCacheable) is { } layered)
                    return layered;
                if (wasJpeg && (compression.ImageDpi is null || scale >= 1)) return null;
                var pixels = image.Width != w || image.Height != h ? Average(image.Pixels, image.Width, image.Height, image.Stride, w, h, _cancel) : image.Pixels;
                _cancel.ThrowIfCancellationRequested();
                var photo = wasJpeg || ManyColors(pixels, image.Stride);
                var samples = Packed(pixels, image.Stride, components);
                Packing? packed = null;
                if (compression.Lossy && photo)
                {
                    // Graphic with many colours (screenshot with soft edges): as a palette if it reaches the minimum quality, which is
                    // sharper than JPEG; real photos miss it and become JPEG
                    if (!wasJpeg && compression.Graphics == GraphicsMethod.Palette && components == 3 && !Premultiplied(dict))
                        packed = Quantized(samples, w, h, dict, compression);
                    // The palette may be somewhat larger than the JPEG (it is sharp), but not much: for photo-like images that barely reach
                    // the minimum quality it would otherwise be a multiple (test file issue14814: 21 → 235 KB). See docs/FEHLERGEDAECHTNIS.md, No. 68.
                    if (_options.ImageCodec?.EncodeJpeg(w, h, image.Stride, pixels, compression.JpegQuality) is { } jpeg &&
                        (packed is null || packed.Data.Length > jpeg.Length * PaletteAllowance))
                        packed = new Packing(jpeg, Dct, null, null, 8);
                    // Without downsampling, JPEG is worth it only if clearly smaller (otherwise better leave it lossless)
                    if (packed is { Filter: var f } p && f == Dct && scale >= 1 && p.Data.Length > raw.Length / 2) packed = null;
                }
                // Lossless (graphics; photos with "lossless" or when JPEG does not help): predictor or exact palette
                if (packed is null && !wasJpeg || packed is null && scale < 1) packed = Lossless(samples, w, h, components, dict, compression.Brotli);
                if (packed is not { } chosen || chosen.Data.Length >= raw.Length) return null;
                var result = new PdfDictionary(dict.Count);
                foreach (var (key, value) in dict.Entries) result.Set(key, value);
                result.Set(Width, PdfInteger.Of(w));
                result.Set(Height, PdfInteger.Of(h));
                result.Set(Bpc, PdfInteger.Of(chosen.Bits));
                result.Set(PdfName.Filter, chosen.Filter);
                if (chosen.Parms is { } parms) result.Set(PdfName.DecodeParms, parms);
                else result.Remove(PdfName.DecodeParms);
                if (chosen.ColorSpace is { } space) result.Set(ColorSpaceKey, space);
                return (chosen.Data, result);
            }
            catch (Exception e) when (e is PdfException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
            {
                return null;
            }
            catch (AggregateException e) when (e.Flatten().InnerExceptions is var inner &&
                                               inner.All(x => x is PdfException or InvalidDataException or IndexOutOfRangeException or ArgumentException))
            {
                // From parallel loops (MRC, masks): as above, only this image stays as it is; the save as a whole does not fail
                return null;
            }
        }

        /// <summary>A palette may be this much larger than the JPEG and still win (it shows edges and text sharply).</summary>
        private const double PaletteAllowance = 1.3;

        /// <summary>Repacked image: data, filter, /DecodeParms, new colour space (palette) or null, bits per value.</summary>
        private sealed record Packing(byte[] Data, PdfName Filter, PdfDictionary? Parms, PdfObject? ColorSpace, int Bits);

        /// <summary>Soft mask with /Matte: the colours are premultiplied, so no colour reduction (the blending would no longer be correct).</summary>
        private static bool Premultiplied(PdfDictionary dict) => dict.Get(SMaskKey) is PdfStream smask && smask.Dictionary.ContainsKey(Matte);

        /// <summary>
        /// Smaller losslessly: Flate with or without PNG predictor; for RGB with at most 256 colours as an exact palette instead
        /// (/Indexed with 1, 2, 4 or 8 bits). With Brotli without predictor (at least one widely used reader ignores /DecodeParms there).
        /// </summary>
        private Packing Lossless(byte[] samples, int width, int height, int components, PdfDictionary dict, bool brotli)
        {
            // Exact palette: at most a third of the bytes even before packing, so RGB is not tried at all
            // Not with a premultiplied mask (ISO 32000-2, 11.6.5.2, Table 144): with /Indexed the palette colours are premultiplied and /Matte
            // counts the components of the base — possible, but kept as it is (the reader unblends either way; docs/FEHLERGEDAECHTNIS.md, No. 562)
            if (components == 3 && !Premultiplied(dict) && PaletteQuantizer.Exact(samples, width * height, 3) is { } exact)
                return Flat(exact.Packed(width, height), width, height, 1, exact.Bits, brotli, IndexedSpace(dict, exact));
            return Flat(samples, width, height, components, 8, brotli, null);
        }

        /// <summary>Flate without and with predictor, whichever is smaller (Brotli: without predictor only).</summary>
        private Packing Flat(byte[] samples, int width, int height, int colors, int bits, bool brotli, PdfObject? space)
        {
            var filter = brotli ? BrotliDecode : FlateDecode;
            if (brotli)
            {
                // kept like the Flate result below: Brotli at quality 11 is by far the slowest step of a run (No. 710)
                var packed = Pack(samples, brotli);
                _plainPacked = (samples, packed, true);
                return new Packing(packed, filter, null, space, bits);
            }
            _cancel.ThrowIfCancellationRequested();
            var rows = PngPredictor.Encode(samples, width, height, colors, bits);
            // A quick trial run decides which variant wins; only the winner is packed at the strongest level (packing both costs
            // 70 % more writing time in a lossless run)
            if (Quick(samples) <= Quick(rows))
            {
                var plain = Pack(samples, brotli: false);
                _plainPacked = (samples, plain, false);
                return new Packing(plain, filter, null, space, bits);
            }
            var predicted = Pack(rows, brotli: false);
            var parms = new PdfDictionary();
            parms.Set(PdfName.Get("Predictor"), PdfInteger.Of(15));
            parms.Set(PdfName.Get("Colors"), PdfInteger.Of(colors));
            parms.Set(Bpc, PdfInteger.Of(bits));
            parms.Set(PdfName.Get("Columns"), PdfInteger.Of(width));
            return new Packing(predicted, filter, parms, space, bits);
        }

        /// <summary>Size with the fastest Flate level, only for comparing two variants.</summary>
        private static long Quick(byte[] data)
        {
            using var counter = new CountingStream();
            using (var z = new ZLibStream(counter, CompressionLevel.Fastest, leaveOpen: true)) z.Write(data);
            return counter.Length;
        }

        /// <summary>Only counts what is written.</summary>
        private sealed class CountingStream : Stream
        {
            private long _length;
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _length;
            public override long Position { get => _length; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => _length += count;
            public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
        }

        /// <summary>[/Indexed base hival lookup]; the base colour space is that of the image (DeviceRGB, ICC, CalRGB).</summary>
        private static PdfArray IndexedSpace(PdfDictionary dict, PaletteImage palette) =>
            new([PdfName.Get("Indexed"), dict.GetRaw(ColorSpaceKey) ?? PdfName.Get("DeviceRGB"), PdfInteger.Of(palette.Count - 1), new PdfString(palette.Palette)]);

        /// <summary>
        /// Palette with colour reduction (lossy only): at most <see cref="CompressionSettings.PaletteColors"/> colours, with
        /// dithering; null if the minimum quality is not reached or the image is too large (above 16 megapixels, which it is not
        /// meant for).
        /// </summary>
        private Packing? Quantized(byte[] samples, int width, int height, PdfDictionary dict, CompressionSettings compression)
        {
            if ((long)width * height > 16_000_000) return null;
            var palette = PaletteQuantizer.Quantize(samples, width, height, compression.PaletteColors, compression.PaletteDither / 100.0, _cancel,
                CompressionSettings.MinimumPsnr(compression.PaletteQuality));
            if (palette is null || palette.Psnr < CompressionSettings.MinimumPsnr(compression.PaletteQuality)) return null;
            return Flat(palette.Packed(width, height), width, height, 1, palette.Bits, compression.Brotli, IndexedSpace(dict, palette));
        }

        /// <summary>
        /// 1-bit images (also stencil masks): the same samples with the selected codec, including existing G4 images if JBIG2 is
        /// selected. Only if the result is smaller.
        /// </summary>
        private (byte[] Data, PdfDictionary Dict)? OneBit(PdfDictionary dict, ReadOnlySpan<byte> raw, int width, int height)
        {
            var rowBytes = (width + 7) / 8;
            // G4 with a column count other than /Width: the rows would be offset, so leave it as it is
            if (StreamFilters.Chain(dict).FirstOrDefault(f => f.Name == "CCITTFaxDecode") is { Name: not null } ccitt &&
                (ccitt.Parms?.GetInteger(PdfName.Get("Columns")) ?? 1728) != width) return null;
            // With a size hint: a crafted image with a huge /Rows would otherwise be decoded up to 1 GB
            var decoded = StreamFilters.Decode(raw.ToArray(), dict, (int)Math.Min(int.MaxValue, (long)rowBytes * height));
            if (decoded.Truncated || decoded.ImageFilter is not null) return null;
            var data = decoded.Data;
            if (data.Length < (long)rowBytes * height) return null;
            var encoded = OneBitEncoded(data, width, height, rowBytes, dict, gray: false);
            return encoded.Data.Length < raw.Length ? encoded : null;
        }

        /// <summary>
        /// Encodes 1-bit samples (rows padded to whole bytes) with the selected codec: JBIG2 (generic region, every pixel as it is,
        /// no pattern matching) or G4. Both decode to the same values.
        /// </summary>
        private (byte[] Data, PdfDictionary Dict) OneBitEncoded(byte[] bits, int width, int height, int rowBytes, PdfDictionary dict, bool gray)
        {
            var jbig2 = _options.Compression?.BilevelCodec == BilevelMethod.Jbig2;
            var data = jbig2 ? Jbig2Encoder.Generic(bits, width, height, rowBytes, _cancel) : CcittEncoder.EncodeG4(bits, width, height, rowBytes, blackIs1: false);
            return (data, OneBitDict(dict, width, height, gray, jbig2));
        }

        /// <summary>
        /// Dictionary for a 1-bit image: /JBIG2Decode or /CCITTFaxDecode with /K −1; <paramref name="gray"/>: made from a grey image
        /// (then DeviceGray, 1 bit, without /Decode).
        /// </summary>
        private static PdfDictionary OneBitDict(PdfDictionary dict, int width, int height, bool gray, bool jbig2)
        {
            var result = new PdfDictionary(dict.Count);
            foreach (var (key, value) in dict.Entries) result.Set(key, value);
            if (jbig2)
            {
                result.Set(PdfName.Filter, Jbig2Name);
                result.Remove(PdfName.DecodeParms);
            }
            else
            {
                var parms = new PdfDictionary();
                parms.Set(PdfName.Get("K"), PdfInteger.Of(-1));
                parms.Set(PdfName.Get("Columns"), PdfInteger.Of(width));
                parms.Set(PdfName.Get("Rows"), PdfInteger.Of(height));
                parms.Set(PdfName.Get("BlackIs1"), PdfBoolean.False);
                result.Set(PdfName.Filter, Ccitt);
                result.Set(PdfName.DecodeParms, parms);
            }
            if (gray)
            {
                result.Set(ColorSpaceKey, DeviceGray);
                result.Set(Bpc, PdfInteger.Of(1));
                result.Remove(DecodeKey);
            }
            return result;
        }

        /// <summary>Components of the colour space if it is grey or RGB (also Cal… and ICC with /N 1 or 3); otherwise 0.</summary>
        private static int Components(PdfObject? space) => space switch
        {
            PdfName { Value: "DeviceGray" or "G" } => 1,
            PdfName { Value: "DeviceRGB" or "RGB" } => 3,
            PdfArray { Count: >= 2 } a when a.Get(0) is PdfName { Value: "CalGray" } => 1,
            PdfArray { Count: >= 2 } a when a.Get(0) is PdfName { Value: "CalRGB" } => 3,
            PdfArray { Count: >= 2 } a when a.Get(0) is PdfName { Value: "ICCBased" } && a.Get(1) is PdfStream icc =>
                (int)(icc.Dictionary.GetInteger(PdfName.N) ?? 0) is var n and (1 or 3) ? n : 0,
            _ => 0,
        };

        /// <summary>
        /// Colour space with one value per pixel (grey, Indexed, Separation, ICC with /N 1): only then is 1-bit data one bit per
        /// pixel and can be written as G4; 1-bit RGB has three bits per pixel.
        /// </summary>
        private static bool OneComponent(PdfObject? space) => Components(space) == 1 || space is PdfArray { Count: >= 2 } a &&
            a.Get(0) is PdfName { Value: "Indexed" or "I" or "Separation" };

        /// <summary>
        /// Samples (8 bits) and their stride per pixel: from Flate &amp; co. directly (1 or 3), from JPEG through the own decoder (grey 1,
        /// colour 3; docs/FEHLERGEDAECHTNIS.md, No. 805 — the same decoder as display and redaction). <paramref name="maxPixels"/>: a JPEG may
        /// come downsampled (by 2, 4 or 8, never below this pixel count). Decoded Flate data is kept in <see cref="_decoded"/> for
        /// <see cref="Repack"/>.
        /// </summary>
        private (byte[] Pixels, int Width, int Height, int Stride)? Samples(PdfDictionary dict, List<(string Name, PdfDictionary? Parms)> chain,
            ReadOnlySpan<byte> raw, int width, int height, int components, long maxPixels)
        {
            var size = (long)width * height * components;
            var decoded = StreamFilters.Decode(raw.ToArray(), dict, (int)Math.Min(size, int.MaxValue));
            if (decoded.Truncated) return null;
            if (decoded.ImageFilter is null)
            {
                _decoded = (dict, decoded.Data);
                return decoded.Data.Length >= size ? (decoded.Data, width, height, components) : null;
            }
            if (decoded.ImageFilter != Dct) return null;
            // Never larger than stated in the PDF: some scanners write a placeholder height into the JPEG header (65,280 rows instead of
            // 3,712, the length only in the DNL marker); the decoder takes /Height then (No. 457), and an image whose size or aspect
            // ratio does not match stays unchanged. See docs/FEHLERGEDAECHTNIS.md, No. 72.
            if (Jpeg(decoded, height, Math.Min(maxPixels, (long)width * height)) is not { } j || j.Width > width || j.Height > height) return null;
            if (Math.Abs((long)j.Width * height - (long)j.Height * width) > Math.Max(width, height)) return null;
            if (maxPixels == long.MaxValue && (j.Width != width || j.Height != height)) return null;
            // grey and RGB as the colour space has them; CMYK stays a JPEG (the codec writes no four components)
            return (components, j.Channels) is (1, 1) or (3, 3) ? (j.Samples, j.Width, j.Height, j.Channels) : null;
        }

        /// <summary>A JPEG through the own decoder, at most <paramref name="maxPixels"/> (reduced by 2, 4 or 8); null if it refuses it.</summary>
        private static JpegImage? Jpeg(StreamFilters.DecodeResult decoded, long pdfHeight, long maxPixels)
        {
            try
            {
                return JpegDecoder.Decode(decoded.Data, new JpegOptions((int?)decoded.ImageParms?.GetInteger(PdfName.Get("ColorTransform")), pdfHeight,
                    maxPixels, PdfLimits.Budget(4L << 30)));
            }
            catch (JpegException)
            {
                return null;
            }
        }

        /// <summary>
        /// How far the decoder may downsample (pixel count for <see cref="JpegOptions.MaxPixels"/>): by 2, 4 or 8 to a size that hits the
        /// target exactly, otherwise only so far that averaging by at least a factor of 2 still follows. A remaining factor between 1 and 2
        /// hardly averages and makes fine patterns flicker (a book cover image from the public test collection at 120 dpi). See
        /// docs/FEHLERGEDAECHTNIS.md, No. 57. (Before, libjpeg in eighths; the own decoder halves — where no power of two fits, it decodes
        /// at full size and the averaging does the rest, never coarser.)
        /// </summary>
        private static long JpegPixels(int width, int height, int w, int h)
        {
            var area = (long)width * height;
            // Small images at full size: nothing is saved there, and ±1 pixel would be a noticeable resampling (1 × 1 mm page: 5 instead of 6 pixels)
            if (area < 4_000_000) return long.MaxValue;
            // the strongest reduction first: by 8, 4, 2 (sizes rounded up, as the decoder computes them); the decoder takes the first scale whose
            // pixels are within the count returned
            for (var s = 8; s >= 2; s /= 2)
            {
                var (sw, sh) = ((width + s - 1) / s, (height + s - 1) / s);
                var exact = Math.Abs(sw - w) <= 1 && Math.Abs(sh - h) <= 1;
                if (exact || sw >= 2 * w && sh >= 2 * h) return (long)sw * sh;
            }
            return long.MaxValue;
        }

        /// <summary>Last decoded Flate data of an image (for <see cref="Repack"/>, which would otherwise decode again).</summary>
        private (PdfDictionary Dict, byte[] Data)? _decoded;

        /// <summary>
        /// Samples last packed at the strongest level or with Brotli, without predictor (the same array as in <see cref="_decoded"/>): if
        /// the image does not become smaller, <see cref="Repack"/> takes this result instead of packing the same bytes again.
        /// </summary>
        private (byte[] Samples, byte[] Packed, bool Brotli)? _plainPacked;

        /// <summary>Colour JPEG? Checked on the one-eighth version (1/64 of the pixels); then it cannot be a black-and-white scan.</summary>
        private static bool Colored(ReadOnlySpan<byte> raw, PdfDictionary dict)
        {
            var decoded = StreamFilters.Decode(raw.ToArray(), dict);
            if (decoded.ImageFilter != Dct || Jpeg(decoded, 0, 1) is not { Channels: 3 } small) return false;
            var pixels = small.Samples;
            for (var o = 0; o + 2 < pixels.Length; o += 3)
                if (Math.Abs(pixels[o] - pixels[o + 1]) > 24 || Math.Abs(pixels[o] - pixels[o + 2]) > 24) return true;
            return false;
        }

        /// <summary>Width and height according to the SOF marker of a JPEG; null if the data does not start with SOI or no SOF precedes the scan data.</summary>
        internal static (int Width, int Height)? JpegSize(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
            for (var at = 2; at + 9 < data.Length;)
            {
                if (data[at] != 0xFF) return null;
                var marker = data[at + 1];
                if (marker == 0xFF)
                {
                    at++;
                    continue;
                }
                var length = data[at + 2] << 8 | data[at + 3];
                // SOF0 … SOF15 except DHT (C4), JPG (C8), DAC (CC)
                if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                    return (data[at + 7] << 8 | data[at + 8], data[at + 5] << 8 | data[at + 6]);
                if (marker == 0xDA || length < 2) return null;
                at += 2 + length;
            }
            return null;
        }

        /// <summary>JPEG: can it be a text scan? Checked on the one-eighth version (<see cref="ScanSegmenter.MayBeTextScan"/>); if in doubt, yes.</summary>
        private static bool MayBeScan(ReadOnlySpan<byte> raw, PdfDictionary dict)
        {
            var decoded = StreamFilters.Decode(raw.ToArray(), dict);
            if (decoded.ImageFilter != Dct || Jpeg(decoded, 0, 1) is not { } small) return true;
            // grey and RGB as they are; CMYK (four channels, not RGBA) cannot be judged this way: if in doubt, yes
            return small.Channels is not (1 or 3) || ScanSegmenter.MayBeTextScan(small.Samples, small.Width, small.Height, small.Channels);
        }

        /// <summary>For Flate: RGB with 3 bytes (from the codec's RGBA) or grey.</summary>
        private static byte[] Packed(byte[] pixels, int stride, int components)
        {
            if (stride == components) return pixels;
            var count = pixels.Length / stride;
            var result = new byte[count * components];
            for (long i = 0, o = 0; i < count; i++, o += components)
                for (var c = 0; c < components; c++) result[o + c] = pixels[i * stride + c];
            return result;
        }

        /// <summary>
        /// Black and white as 1-bit rows (0 = black) if the image has only two values, or, with <paramref name="nearly"/>, almost only
        /// two (at most 0.5 % intermediate tones, as with scanned text); otherwise null.
        /// </summary>
        private static byte[]? Bilevel(byte[] samples, int components, bool nearly, int width, int height)
        {
            var pixels = width * height;
            if (pixels < 64 * 64) return null;
            long middle = 0, ink = 0, limit = nearly ? pixels / 200 : 0;
            for (var i = 0; i < pixels; i++)
            {
                var v = Luma(samples, i, components, out var colored);
                if (colored) return null;
                // Nearly black and white: light tints (grey table areas) count too; otherwise they would silently become white
                if (nearly ? v is > 48 and < 240 : v is > 0 and < 255) middle++;
                else if (v <= 48) ink++;
                if (middle > limit) return null;
            }
            // Nearly black and white only with real ink (scan of a text page): otherwise a nearly white image with a few soft spots
            // (the background of a compact image PDF, a light gradient) would always pass, because the intermediate tones count
            // against all pixels, and the spots would become black or white. See docs/FEHLERGEDAECHTNIS.md, No. 55.
            if (nearly && middle > 0 && ink < pixels / 100) return null;
            var rowBytes = (width + 7) / 8;
            var bits = new byte[rowBytes * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    if (Luma(samples, y * width + x, components, out _) >= 128) bits[y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            return bits;
        }

        /// <summary>Luminance of a pixel; <paramref name="colored"/> if it is clearly coloured (then it is not a black-and-white image).</summary>
        private static int Luma(byte[] samples, int pixel, int components, out bool colored)
        {
            colored = false;
            var o = pixel * components;
            if (components == 1) return samples[o];
            int r = samples[o], g = samples[o + 1], b = samples[o + 2];
            colored = Math.Abs(r - g) > 24 || Math.Abs(r - b) > 24;
            return (r * 77 + g * 150 + b * 29) >> 8;
        }

        /// <summary>
        /// Downsampling by averaging over the area of each target pixel (like "Average" in common PDF editors): calm, without staircases, fast.
        /// </summary>
        internal static byte[] Average(byte[] samples, int width, int height, int components, int newWidth, int newHeight, CancellationToken cancel = default)
        {
            var result = new byte[newWidth * newHeight * components];
            var sums = new long[newWidth * components];
            var counts = new int[newWidth];
            // Compute the column boundaries once
            var x0 = new int[newWidth + 1];
            for (var x = 0; x <= newWidth; x++) x0[x] = (int)((long)x * width / newWidth);
            for (var ty = 0; ty < newHeight; ty++)
            {
                if ((ty & 63) == 0) cancel.ThrowIfCancellationRequested();
                int y0 = (int)((long)ty * height / newHeight), y1 = Math.Max(y0 + 1, (int)((long)(ty + 1) * height / newHeight));
                Array.Clear(sums);
                Array.Clear(counts);
                for (var y = y0; y < y1 && y < height; y++)
                {
                    var row = y * width * components;
                    for (var tx = 0; tx < newWidth; tx++)
                    {
                        var end = Math.Max(x0[tx] + 1, x0[tx + 1]);
                        for (var x = x0[tx]; x < end && x < width; x++)
                        {
                            for (var c = 0; c < components; c++) sums[tx * components + c] += samples[row + x * components + c];
                            counts[tx]++;
                        }
                    }
                }
                var o = ty * newWidth * components;
                for (var tx = 0; tx < newWidth; tx++)
                    for (var c = 0; c < components; c++)
                        result[o + tx * components + c] = (byte)((sums[tx * components + c] + counts[tx] / 2) / Math.Max(1, counts[tx]));
            }
            return result;
        }

        /// <summary>
        /// Photo rather than graphic? More than 256 different colours in a sample, or for grey more than 96 of the 256 levels
        /// (graphics and screenshots have few; grey can never have more than 256).
        /// </summary>
        private static bool ManyColors(byte[] pixels, int components)
        {
            var count = pixels.Length / components;
            var step = Math.Max(1, count / 20_000);
            var limit = components == 1 ? 96 : 256;
            var seen = new HashSet<int>();
            for (var i = 0; i < count; i += step)
            {
                var o = i * components;
                seen.Add(components == 1 ? pixels[o] : pixels[o] << 16 | pixels[o + 1] << 8 | pixels[o + 2]);
                if (seen.Count > limit) return true;
            }
            return false;
        }
    }
}
