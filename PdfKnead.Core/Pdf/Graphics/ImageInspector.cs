using PdfKnead.Core.Imaging;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Graphics;

/// <summary>Method with which an image is stored in the PDF, taken from the last filter of the chain.</summary>
public enum ImageCodec
{
    /// <summary>No filter, or the last filter is an ASCII or Crypt filter.</summary>
    Uncompressed,

    /// <summary>DCTDecode (ITU-T T.81).</summary>
    Jpeg,

    /// <summary>JPXDecode (ITU-T T.800).</summary>
    Jpeg2000,

    /// <summary>JBIG2Decode (ITU-T T.88).</summary>
    Jbig2,

    /// <summary>CCITTFaxDecode (ITU-T T.4/T.6).</summary>
    Ccitt,

    /// <summary>FlateDecode.</summary>
    Flate,

    /// <summary>LZWDecode.</summary>
    Lzw,

    /// <summary>RunLengthDecode.</summary>
    RunLength,

    /// <summary>Any other filter; see <see cref="ImageInfo.FilterName"/>.</summary>
    Other,
}

/// <summary>
/// Information about an image on a page (image info tool): how it is stored, how large it appears on the page
/// (and from that the effective resolution), what the image itself contains (EXIF …) and what suggests a scan. Information only;
/// nothing is changed.
/// </summary>
public sealed record ImageInfo
{
    /// <summary>Storage method of the image data.</summary>
    public ImageCodec Codec { get; init; }

    /// <summary>Name of the last filter if the method is unknown (<see cref="ImageCodec.Other"/>).</summary>
    public string? FilterName { get; init; }

    /// <summary>CCITT: parameter K (negative = Group 4, 0 = Group 3 one-dimensional, positive = Group 3 two-dimensional).</summary>
    public long? CcittK { get; init; }

    /// <summary>Flate/LZW with a PNG or TIFF predictor.</summary>
    public bool Predictor { get; init; }

    /// <summary>Width in pixels according to the image dictionary.</summary>
    public int Width { get; init; }

    /// <summary>Height in pixels according to the image dictionary.</summary>
    public int Height { get; init; }

    /// <summary>Bits per component (1 for stencil masks); 0 if not given (for example JPEG 2000).</summary>
    public int BitsPerComponent { get; init; }

    /// <summary>Colour space: "DeviceRGB", "ICCBased", "Indexed" …; null if the image brings its own (JPEG 2000) or for stencil masks.</summary>
    public string? ColorSpace { get; init; }

    /// <summary>Channels of the colour space (for Indexed those of the base).</summary>
    public int Components { get; init; }

    /// <summary>Indexed: number of colours in the palette.</summary>
    public int? PaletteColors { get; init; }

    /// <summary>Indexed: colour space of the palette.</summary>
    public string? PaletteBase { get; init; }

    /// <summary>Separation/DeviceN: names of the colourants (at most eight for DeviceN, comma-separated).</summary>
    public string? SpotColors { get; init; }

    /// <summary>Name of the ICC profile (from /ICCBased or from the image).</summary>
    public string? IccProfile { get; init; }

    /// <summary>Stencil mask (/ImageMask): 1 bit, painted in the fill colour.</summary>
    public bool Stencil { get; init; }

    /// <summary>Own opacity mask (/SMask, or JPEG 2000 with alpha via /SMaskInData).</summary>
    public bool SoftMask { get; init; }

    /// <summary>Mask as an image or a colour key range (/Mask).</summary>
    public bool Mask { get; init; }

    /// <summary>Bytes in the file; for inline images the size of their data.</summary>
    public long StoredBytes { get; init; }

    /// <summary>Inline image (BI … EI in the content stream).</summary>
    public bool Inline { get; init; }

    /// <summary>Image inserted in PDF Knead (an element that remains editable after saving): only pixels and placement.</summary>
    public bool FromElement { get; init; }

    /// <summary>Width on the page in points (along the image's own x axis, also when rotated; /UserUnit applied).</summary>
    public double WidthPoints { get; init; }

    /// <summary>Height on the page in points (along the image's own y axis, also when rotated; /UserUnit applied).</summary>
    public double HeightPoints { get; init; }

    /// <summary>Rotation as seen (including the rotation of the page), in degrees counter-clockwise (0 … 359), rounded.</summary>
    public int Rotation { get; init; }

    /// <summary>Mirrored.</summary>
    public bool Mirrored { get; init; }

    /// <summary>Skewed (edges not at right angles).</summary>
    public bool Skewed { get; init; }

    /// <summary>Only partly visible (clipped).</summary>
    public bool Clipped { get; init; }

    /// <summary>Covers (almost) the whole page (at least 85 % of the crop box).</summary>
    public bool CoversPage { get; init; }

    /// <summary>Invisible text over the image (text layer of an OCR run): number of characters.</summary>
    public int HiddenTextGlyphs { get; init; }

    /// <summary>What the image itself contains (JPEG, JPEG 2000); null if nothing could be read.</summary>
    public ImageMetadata? Embedded { get; init; }

    /// <summary>/Producer of the document information, cleaned.</summary>
    public string? Producer { get; init; }

    /// <summary>/Creator of the document information, cleaned.</summary>
    public string? Creator { get; init; }

    /// <summary>Corners of the image in page space (bottom left, bottom right, top right, top left), for the highlight frame.</summary>
    public (double X, double Y)[] Corners { get; init; } = [];

    /// <summary>Effective horizontal resolution on the page in dpi; 0 if the width is unknown.</summary>
    public double DpiX => WidthPoints > 0 ? Width / (WidthPoints / 72) : 0;

    /// <summary>Effective vertical resolution on the page in dpi; 0 if the height is unknown.</summary>
    public double DpiY => HeightPoints > 0 ? Height / (HeightPoints / 72) : 0;

    private static readonly int[] ScanResolutions = [150, 200, 240, 300, 400, 600, 1200];

    /// <summary>Resolution as scanners deliver it (±2 %), equal in both directions.</summary>
    public bool TypicalScanResolution =>
        ScanResolutions.Any(r => Math.Abs(DpiX - r) <= r * 0.02) && Math.Abs(DpiX - DpiY) <= Math.Max(DpiX, DpiY) * 0.02;

    /// <summary>Bilevel image: a method for black-and-white scans, or 1 bit with a single channel.</summary>
    public bool Bilevel => Codec is ImageCodec.Ccitt or ImageCodec.Jbig2 || BitsPerComponent == 1 && Components <= 1;

    /// <summary>Probably a scan: covers the page and has a typical feature (bilevel, scan resolution, OCR text).</summary>
    public bool LikelyScan => !FromElement && CoversPage && (Bilevel || TypicalScanResolution || HiddenTextGlyphs >= 10);
}

/// <summary>One row of the display: label and value, already translated.</summary>
/// <param name="Label">The label.</param>
/// <param name="Value">The value.</param>
public sealed record ImageInfoRow(string Label, string Value);

/// <summary>One section of the display: heading and rows.</summary>
/// <param name="Title">The heading, already translated.</param>
/// <param name="Rows">The rows.</param>
public sealed record ImageInfoSection(string Title, IReadOnlyList<ImageInfoRow> Rows)
{
    /// <summary>Creates a section from label/value pairs.</summary>
    /// <param name="title">The heading, already translated.</param>
    /// <param name="rows">The rows as label/value pairs.</param>
    public ImageInfoSection(string title, IEnumerable<(string Label, string Value)> rows) : this(title, rows.Select(r => new ImageInfoRow(r.Label, r.Value)).ToList()) { }
}

/// <summary>
/// Image info: which image lies at a point of the page and what can be said about it. Only the image dictionary and, for
/// JPEG/JPEG 2000, the header of the data are read; nothing is decoded.
/// </summary>
public static class ImageInspector
{
    /// <summary>Read at most this much of the image header (EXIF and XMP are at the start; a segment has at most 64 KB).</summary>
    private const int HeaderBytes = 2 << 20;

    private const int MaxIccStream = 4 << 20;

    private static readonly PdfName WidthKey = PdfName.Get("Width"), W = PdfName.Get("W");
    private static readonly PdfName HeightKey = PdfName.Get("Height"), H = PdfName.Get("H");
    private static readonly PdfName BitsKey = PdfName.Get("BitsPerComponent"), Bpc = PdfName.Get("BPC");
    private static readonly PdfName ColorSpaceKey = PdfName.Get("ColorSpace"), Cs = PdfName.Get("CS");
    private static readonly PdfName ImageMaskKey = PdfName.Get("ImageMask"), Im = PdfName.Get("IM");
    private static readonly PdfName SMaskKey = PdfName.Get("SMask"), SMaskInData = PdfName.Get("SMaskInData"), MaskKey = PdfName.Get("Mask");
    private static readonly PdfName K = PdfName.Get("K");

    /// <summary>
    /// Images under a point in page space, topmost first (entry indices). Only images count: text, drawings and annotations
    /// on top do not hide them for the image info.
    /// </summary>
    /// <param name="list">The display list of the page.</param>
    /// <param name="x">x in page space.</param>
    /// <param name="y">y in page space.</param>
    /// <returns>Indices of the image entries, topmost first; empty if there is none.</returns>
    public static List<int> ImagesAt(DisplayList list, double x, double y)
    {
        var items = list.Items.Span;
        using var hits = list.QueryHits(RectD.FromLtrb(x - 0.5, y - 0.5, x + 0.5, y + 0.5));
        var found = new List<int>();
        for (var i = 0; i < hits.Count; i++)
        {
            var index = hits[i];
            var item = items[index];
            if (item.Kind != DisplayItemKind.Image) continue;
            // inside the unit square of the image? (Without area — matrix not invertible — it cannot be seen.)
            var matrix = list.Matrices[item.Matrix];
            if (!matrix.IsInvertible) continue;
            var (u, v) = matrix.Invert().Transform(x, y);
            if (!double.IsFinite(u) || !double.IsFinite(v) || u < -1e-6 || u > 1 + 1e-6 || v < -1e-6 || v > 1 + 1e-6) continue;
            if (item.Clip != 0 && list.Clips[item.Clip].Bounds is var clip && (x < clip.Left || x > clip.Right || y < clip.Top || y > clip.Bottom)) continue;
            found.Add(index);
        }
        found.Sort((a, b) => b.CompareTo(a));   // drawn later = further up
        return found;
    }

    /// <summary>Information about entry <paramref name="item"/> of the display list of a page.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="list">The display list of the page.</param>
    /// <param name="item">Index of an image entry (from <see cref="ImagesAt"/>).</param>
    /// <returns>The information.</returns>
    public static ImageInfo Inspect(PdfDocument doc, PdfPage page, DisplayList list, int item)
    {
        var entry = list.Items.Span[item];
        var source = list.Images[entry.Image];
        var dict = source.Dictionary;
        var matrix = list.Matrices[entry.Matrix];
        var info = Describe(doc, dict, source) with { Inline = source.Inline is not null };
        info = Placement(info, matrix, page);
        if (entry.Clip != 0 && list.Clips[entry.Clip].Bounds is var clip
            && (clip.Left > entry.Left + 1 || clip.Right < entry.Right - 1 || clip.Top > entry.Top + 1 || clip.Bottom < entry.Bottom - 1))
            info = info with { Clipped = true };
        // invisible text over the image: the text layer of an OCR run
        var hidden = 0;
        foreach (var glyph in list.Text)
        {
            if (glyph.Visible) continue;
            var cx = (glyph.X0 + glyph.X2) / 2;
            var cy = (glyph.Y0 + glyph.Y2) / 2;
            if (cx >= entry.Left && cx <= entry.Right && cy >= entry.Top && cy <= entry.Bottom) hidden++;
        }
        var meta = doc.Info;
        return info with { HiddenTextGlyphs = hidden, Producer = Clean(meta.Producer), Creator = Clean(meta.Creator) };
    }

    /// <summary>The quality a JPEG image was saved with (estimated from its quantisation table); null for other images or if unknown.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="source">The image.</param>
    /// <returns>Quality 1 … 100, or null.</returns>
    public static int? JpegQuality(PdfDocument doc, ImageSource source)
    {
        var info = Describe(doc, source.Dictionary, source);
        return info.Codec == ImageCodec.Jpeg ? info.Embedded?.Quality : null;
    }

    /// <summary>Information about an image inserted in PDF Knead (element): pixels and placement; it is only encoded when saved.</summary>
    /// <param name="element">The image element.</param>
    /// <param name="page">The page it lies on.</param>
    /// <returns>The information.</returns>
    public static ImageInfo Inspect(Editing.ImageElement element, PdfPage page)
    {
        // unit square → upright space of the element (/BBox 0 0 width height) → page, including rotation
        var size = element.UprightSize;
        var unit = Matrix.Scale(size.Width, size.Height) * Editing.ElementAppearance.For(element).UprightToPage();
        var info = new ImageInfo
        {
            FromElement = true,
            Codec = ImageCodec.Uncompressed,
            Width = element.Image.Width,
            Height = element.Image.Height,
            BitsPerComponent = 8,
            ColorSpace = "DeviceRGB",
            Components = 3,
            SoftMask = true,
        };
        return Placement(info, unit, page);
    }

    private static ImageInfo Placement(ImageInfo info, Matrix m, PdfPage page)
    {
        var unit = page.UserUnit;
        var width = Math.Sqrt(m.A * m.A + m.B * m.B) * unit;
        var height = Math.Sqrt(m.C * m.C + m.D * m.D) * unit;
        // counter-clockwise in page space; /Rotate turns the display clockwise
        var rotation = (int)Math.Round(Math.Atan2(m.B, m.A) * 180 / Math.PI) - page.Rotate;
        rotation = (rotation % 360 + 360) % 360;
        var mirrored = m.Determinant < 0;
        // right-angled? dot product of the edges relative to their lengths
        var skewed = width > 0 && height > 0 && Math.Abs(m.A * m.C + m.B * m.D) * unit * unit / (width * height) > 0.01;
        var corners = new[] { m.Transform(0, 0), m.Transform(1, 0), m.Transform(1, 1), m.Transform(0, 1) };
        var crop = page.CropBox;
        double left = corners.Min(c => c.X), right = corners.Max(c => c.X), bottom = corners.Min(c => c.Y), top = corners.Max(c => c.Y);
        var overlap = Math.Max(0, Math.Min(right, crop.Right) - Math.Max(left, crop.Left)) * Math.Max(0, Math.Min(top, crop.Top) - Math.Max(bottom, crop.Bottom));
        var covers = crop.Width > 0 && crop.Height > 0 && overlap >= crop.Width * crop.Height * 0.85;
        return info with
        {
            WidthPoints = width, HeightPoints = height, Rotation = rotation, Mirrored = mirrored, Skewed = skewed, CoversPage = covers, Corners = corners,
        };
    }

    private static PdfObject Get(PdfDictionary dict, PdfName key, PdfName shortKey) =>
        dict.Get(key) is not PdfNull and var value ? value : dict.Get(shortKey);

    /// <summary>What the image dictionary says, plus the header of the data for JPEG/JPEG 2000.</summary>
    private static ImageInfo Describe(PdfDocument doc, PdfDictionary dict, ImageSource source)
    {
        var chain = StreamFilters.Chain(dict);
        var last = chain.Count > 0 ? chain[^1] : default;
        var codec = last.Name switch
        {
            null => ImageCodec.Uncompressed,
            "DCTDecode" => ImageCodec.Jpeg,
            "JPXDecode" => ImageCodec.Jpeg2000,
            "JBIG2Decode" => ImageCodec.Jbig2,
            "CCITTFaxDecode" => ImageCodec.Ccitt,
            "FlateDecode" => ImageCodec.Flate,
            "LZWDecode" => ImageCodec.Lzw,
            "RunLengthDecode" => ImageCodec.RunLength,
            "ASCIIHexDecode" or "ASCII85Decode" or "Crypt" => ImageCodec.Uncompressed,
            _ => ImageCodec.Other,
        };
        var stencil = Get(dict, ImageMaskKey, Im) is PdfBoolean { Value: true };
        var info = new ImageInfo
        {
            Codec = codec,
            FilterName = codec == ImageCodec.Other ? Clean(last.Name) : null,
            CcittK = codec == ImageCodec.Ccitt ? last.Parms?.GetInteger(K) ?? 0 : null,
            Predictor = codec is ImageCodec.Flate or ImageCodec.Lzw && Predictor.Of(last.Parms).Kind >= 2,
            Width = (int)Math.Clamp(Get(dict, WidthKey, W).AsNumber() ?? 0, 0, int.MaxValue),
            Height = (int)Math.Clamp(Get(dict, HeightKey, H).AsNumber() ?? 0, 0, int.MaxValue),
            BitsPerComponent = stencil ? 1 : (int)Math.Clamp(Get(dict, BitsKey, Bpc).AsNumber() ?? 0, 0, 32),
            Stencil = stencil,
            SoftMask = dict.Get(SMaskKey) is PdfStream || dict.GetInteger(SMaskInData) is 1 or 2,
            Mask = dict.Get(MaskKey) is PdfStream or PdfArray,
            StoredBytes = source.Stream?.DataLength ?? source.Inline?.Data.Length ?? 0,
        };
        if (!stencil) info = ColorSpace(doc, info, Get(dict, ColorSpaceKey, Cs), source.Resources);

        // header of the image data: only for JPEG and JPEG 2000 (other methods carry no information), decrypted, without the other filters
        if (codec is ImageCodec.Jpeg or ImageCodec.Jpeg2000)
        {
            var head = Head(doc, source, chain.Count);
            var embedded = head is null ? null : codec == ImageCodec.Jpeg ? ImageMetadata.FromJpeg(head) : ImageMetadata.FromJpx(head);
            if (embedded is not null) info = info with { Embedded = embedded, IccProfile = info.IccProfile ?? embedded.IccProfile };
        }
        return info;
    }

    /// <summary>Start of the image data in its image format; null if it cannot be read.</summary>
    private static byte[]? Head(PdfDocument doc, ImageSource source, int filters)
    {
        try
        {
            if (source.Inline is { } inline) return filters == 1 ? inline.Data[..Math.Min(inline.Data.Length, HeaderBytes)] : null;
            if (source.Stream is not { } stream) return null;
            // only the image format as a filter: the start suffices (encrypted files: decrypt completely; the data is small enough
            // anyway, since it is also read for drawing)
            if (filters == 1 && !doc.IsEncrypted) return doc.StoredStreamData(stream)[..(int)Math.Min(stream.DataLength, HeaderBytes)].ToArray();
            var data = doc.DecodeStream(stream).Data;
            return data.Length > HeaderBytes ? data[..HeaderBytes] : data;
        }
        catch (Exception e) when (e is PdfException or IOException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static ImageInfo ColorSpace(PdfDocument doc, ImageInfo info, PdfObject space, PdfDictionary? resources, int depth = 0)
    {
        // crafted files: a palette whose base is the palette itself would otherwise recurse endlessly
        if (depth > 3) return info;
        // name from the resources (/ColorSpace << /CS0 … >>)
        if (space is PdfName named && named.Value is not ("DeviceGray" or "G" or "DeviceRGB" or "RGB" or "DeviceCMYK" or "CMYK" or "Pattern")
            && resources?.GetDictionary(ColorSpaceKey)?.Get(named) is not PdfNull and { } resolved)
            space = resolved;
        var family = space switch
        {
            PdfName n => n.Value,
            PdfArray { Count: > 0 } a when a.Get(0) is PdfName n => n.Value,
            _ => null,
        };
        switch (family)
        {
            case null:
                return info;
            case "DeviceGray" or "G" or "CalGray":
                return info with { ColorSpace = family == "G" ? "DeviceGray" : family, Components = 1 };
            case "DeviceRGB" or "RGB" or "CalRGB":
                return info with { ColorSpace = family == "RGB" ? "DeviceRGB" : family, Components = 3 };
            case "DeviceCMYK" or "CMYK":
                return info with { ColorSpace = "DeviceCMYK", Components = 4 };
            case "Lab":
                return info with { ColorSpace = "Lab", Components = 3 };
            case "ICCBased" when space is PdfArray a && a.Get(1) is PdfStream profile:
                string? name = null;
                if (profile.DataLength <= MaxIccStream)
                {
                    try
                    {
                        name = ImageMetadata.IccName(doc.GetStreamData(profile));
                    }
                    catch (Exception e) when (e is PdfException or IOException or ArgumentException or InvalidOperationException)
                    {
                    }
                }
                var n = (int)Math.Clamp(profile.Dictionary.GetInteger(PdfName.Get("N")) ?? 3, 1, 4);
                return info with { ColorSpace = "ICCBased", Components = n, IccProfile = name };
            case "Indexed" or "I" when space is PdfArray a:
                var baseInfo = ColorSpace(doc, new ImageInfo(), a.Get(1), resources, depth + 1);
                var colors = (int)Math.Clamp((a.GetNumber(2) ?? 255) + 1, 1, 256);
                return info with
                {
                    ColorSpace = "Indexed", Components = baseInfo.Components, PaletteColors = colors, PaletteBase = baseInfo.ColorSpace,
                    IccProfile = baseInfo.IccProfile,
                };
            case "Separation" when space is PdfArray a:
                return info with { ColorSpace = "Separation", Components = 1, SpotColors = Clean((a.Get(1) as PdfName)?.Value) };
            case "DeviceN" when space is PdfArray a:
                var names = a.Get(1) as PdfArray;
                return info with
                {
                    ColorSpace = "DeviceN", Components = names?.Count ?? 0,
                    SpotColors = names is null ? null : Clean(string.Join(", ", names.Take(8).Select(o => (o as PdfName)?.Value).OfType<string>())),
                };
            default:
                return info with { ColorSpace = Clean(family) };
        }
    }

    // ------------------------------------------------------------ Display

    /// <summary>The information as sections with translated rows (the image panel and <c>pdf-knead bilder</c>).</summary>
    /// <param name="info">The information.</param>
    /// <returns>The sections in display order; sections without rows are left out.</returns>
    public static IReadOnlyList<ImageInfoSection> Sections(ImageInfo info)
    {
        var c = Tr.Culture;
        string N(double value, int decimals = 0) => value.ToString("N" + decimals, c);
        var sections = new List<ImageInfoSection>();

        // image
        var image = new List<(string, string)> { (Tr.T("Format"), Format(info)) };
        if (info.Width > 0 && info.Height > 0)
        {
            var pixels = $"{N(info.Width)} × {N(info.Height)}";
            // the PDF gives other dimensions than the JPEG itself (crafted or faulty producer): show both
            if (info.Codec == ImageCodec.Jpeg && info.Embedded?.Size is { } own && own != (info.Width, info.Height) && own.Width > 0 && own.Height > 0)
                pixels = Tr.T("{0} laut PDF, {1} × {2} im JPEG selbst", pixels, N(own.Width), N(own.Height));
            image.Add((Tr.T("Pixel"), pixels));
        }
        image.Add((Tr.T("Farbraum"), ColorSpaceText(info)));
        image.Add((Tr.T("Bittiefe"), Depth(info)));
        if (info.IccProfile is { } icc) image.Add((Tr.T("Farbprofil"), icc));
        if (info.Embedded is { } jpeg && info.Codec == ImageCodec.Jpeg)
        {
            var details = new List<string>();
            if (jpeg.Quality is { } q) details.Add(Tr.T("Qualität etwa {0}", q));
            if (jpeg.Subsampling is { } s) details.Add(s);
            if (jpeg.Progressive) details.Add(Tr.T("progressiv"));
            if (jpeg.Arithmetic) details.Add(Tr.T("arithmetisch kodiert"));
            if (details.Count > 0) image.Add((Tr.T("JPEG"), string.Join(" · ", details)));
        }
        if (info.SoftMask || info.Mask) image.Add((Tr.T("Transparenz"), info.SoftMask ? Tr.T("ja, mit eigener Deckkraft-Maske") : Tr.T("ja, mit Maske")));
        if (!info.FromElement)
        {
            var stored = Bytes(info.StoredBytes, c);
            var raw = (long)info.Width * info.Height * Math.Max(1, info.Components) * Math.Max(1, info.BitsPerComponent) / 8;
            // compression ratio only when it is meaningful (tiny images are larger when compressed, a blank page below 1 %)
            var percent = raw > 0 ? 100.0 * info.StoredBytes / raw : 0;
            if (info.Codec != ImageCodec.Uncompressed && raw >= 4096 && percent is >= 1 and <= 100)
                stored = Tr.T("{0} (ungepackt {1}, {2} %)", stored, Bytes(raw, c), N(percent));
            else if (info.Codec != ImageCodec.Uncompressed && raw >= 4096 && percent < 1)
                stored = Tr.T("{0} (ungepackt {1})", stored, Bytes(raw, c));
            image.Add((Tr.T("In der Datei"), info.Inline ? Tr.T("{0}, direkt im Seiteninhalt", stored) : stored));
        }
        else image.Add((Tr.T("In der Datei"), Tr.T("beim Speichern gepackt (in PDF-Knead eingefügt, bleibt bearbeitbar)")));
        sections.Add(new ImageInfoSection(Tr.T("Bild"), image));

        // on the page
        var placed = new List<(string, string)>
        {
            (Tr.T("Größe"), Tr.T("{0} × {1} mm", N(info.WidthPoints / 72 * 25.4, 1), N(info.HeightPoints / 72 * 25.4, 1))),
            (Tr.T("Auflösung"), Math.Abs(info.DpiX - info.DpiY) <= Math.Max(info.DpiX, info.DpiY) * 0.01
                ? Tr.T("{0} dpi", N(info.DpiX))
                : Tr.T("{0} × {1} dpi", N(info.DpiX), N(info.DpiY))),
        };
        if (info.Rotation != 0)
            placed.Add((Tr.T("Gedreht"), info.Rotation <= 180 ? Tr.T("{0}° gegen den Uhrzeigersinn", info.Rotation) : Tr.T("{0}° im Uhrzeigersinn", 360 - info.Rotation)));
        if (info.Mirrored) placed.Add((Tr.T("Gespiegelt"), Tr.T("ja")));
        if (info.Skewed) placed.Add((Tr.T("Verzerrt"), Tr.T("ja")));
        if (info.Clipped) placed.Add((Tr.T("Beschnitten"), Tr.T("ja, nur ein Teil ist zu sehen")));
        sections.Add(new ImageInfoSection(Tr.T("Auf der Seite"), placed));

        // stored in the image
        if (info.Embedded is { } e)
        {
            var rows = new List<(string, string)>();
            if (Join(e.Make, e.Model) is { } device) rows.Add((Tr.T("Gerät"), device));
            if (e.Software is { } software) rows.Add((Tr.T("Software"), software));
            if (e.Taken is { } taken) rows.Add((Tr.T("Datum"), taken.ToString("g", c)));
            if (e.StoredDpi is { } dpi)
                rows.Add((Tr.T("Gespeicherte Auflösung"), Math.Abs(dpi.X - dpi.Y) < 0.5 ? Tr.T("{0} dpi", N(dpi.X)) : Tr.T("{0} × {1} dpi", N(dpi.X), N(dpi.Y))));
            if (e.Orientation is { } o) rows.Add((Tr.T("Ausrichtung"), Orientation(o)));
            if (e.JpxColor is { } jpx)
                rows.Add((Tr.T("Farbraum im Bild"), jpx switch { 16 => "sRGB", 17 => Tr.T("Graustufen"), 18 => "sYCC", 12 => "CMYK", 14 => "Lab", _ => jpx.ToString(c) }));
            if (e.Artist is { } artist) rows.Add((Tr.T("Urheber"), artist));
            if (e.Copyright is { } copyright) rows.Add((Tr.T("Copyright"), copyright));
            if (e.Gps is { } gps)
                rows.Add((Tr.T("Ort (GPS)"), $"{gps.Latitude.ToString("0.00000", System.Globalization.CultureInfo.InvariantCulture)}, " +
                                           gps.Longitude.ToString("0.00000", System.Globalization.CultureInfo.InvariantCulture)));
            if (rows.Count > 0) sections.Add(new ImageInfoSection(Tr.T("Im Bild gespeichert"), rows));
        }

        // origin
        if (!info.FromElement)
        {
            var origin = new List<(string, string)>();
            if (info.LikelyScan) origin.Add((Tr.T("Einschätzung"), Tr.T("vermutlich ein Scan")));
            var hints = new List<string>();
            if (info.CoversPage)
            {
                hints.Add(Tr.T("bedeckt die ganze Seite"));
                if (info.Codec is ImageCodec.Ccitt or ImageCodec.Jbig2) hints.Add(Tr.T("Schwarzweiß-Verfahren für Scans"));
                else if (info.Bilevel) hints.Add(Tr.T("Schwarzweiß, 1 Bit"));
                if (info.TypicalScanResolution) hints.Add(Tr.T("typische Scan-Auflösung"));
            }
            if (info.HiddenTextGlyphs >= 10) hints.Add(Tr.T("unsichtbarer Text darüber (Texterkennung, durchsuchbar)"));
            if (hints.Count > 0) origin.Add((Tr.T("Hinweise"), string.Join(" · ", hints)));
            if (info.Creator is { } creator) origin.Add((Tr.T("PDF erstellt mit"), creator));
            if (info.Producer is { } producer) origin.Add((Tr.T("PDF erzeugt von"), producer));
            if (origin.Count > 0) sections.Add(new ImageInfoSection(Tr.T("Herkunft"), origin));
        }
        return sections;

        string Format(ImageInfo i) => i.Codec switch
        {
            ImageCodec.Jpeg => "JPEG",
            ImageCodec.Jpeg2000 => "JPEG 2000",
            ImageCodec.Jbig2 => Tr.T("JBIG2 (Schwarzweiß)"),
            ImageCodec.Ccitt => i.CcittK < 0 ? Tr.T("CCITT Gruppe 4 (Fax, Schwarzweiß)") : Tr.T("CCITT Gruppe 3 (Fax, Schwarzweiß)"),
            ImageCodec.Flate => i.Predictor ? Tr.T("verlustfrei gepackt (Flate, mit Prädiktor)") : Tr.T("verlustfrei gepackt (Flate)"),
            ImageCodec.Lzw => Tr.T("verlustfrei gepackt (LZW)"),
            ImageCodec.RunLength => Tr.T("verlustfrei gepackt (Lauflänge)"),
            ImageCodec.Other => i.FilterName ?? "?",
            _ => i.FromElement ? Tr.T("in PDF-Knead eingefügtes Bild") : Tr.T("ungepackt"),
        };

        string Space(string? space, int components) => space switch
        {
            "DeviceGray" or "CalGray" => Tr.T("Graustufen"),
            "DeviceRGB" or "CalRGB" => "RGB",
            "DeviceCMYK" => "CMYK",
            "Lab" => "Lab",
            "ICCBased" => Tr.T("{0} mit Farbprofil", components switch { 1 => Tr.T("Graustufen"), 4 => "CMYK", _ => "RGB" }),
            null => Tr.T("unbekannt"),
            var other => other,
        };

        string ColorSpaceText(ImageInfo i)
        {
            if (i.Stencil) return Tr.T("keiner (Schablone in der Füllfarbe)");
            if (i.ColorSpace is null && i.Codec == ImageCodec.Jpeg2000) return Tr.T("im Bild festgelegt");
            if (i.BitsPerComponent == 1 && i.Components == 1 && i.ColorSpace is "DeviceGray" or "CalGray" or "ICCBased") return Tr.T("Schwarzweiß");
            return i.ColorSpace switch
            {
                "Indexed" => Tr.T("Palette mit {0} Farben ({1})", i.PaletteColors ?? 0, Space(i.PaletteBase, i.Components)),
                "Separation" => Tr.T("Sonderfarbe „{0}“", i.SpotColors ?? ""),
                "DeviceN" => Tr.T("{0} Farbauszüge ({1})", i.Components, i.SpotColors ?? ""),
                _ => Space(i.ColorSpace, i.Components),
            };
        }

        string Depth(ImageInfo i)
        {
            var b = i.BitsPerComponent;
            if (b <= 0) return i.Codec == ImageCodec.Jpeg2000 ? Tr.T("im Bild festgelegt") : Tr.T("unbekannt");
            if (i.Stencil || b == 1 && i.Components <= 1) return Tr.T("1 Bit je Pixel");
            if (i.ColorSpace == "Indexed") return Tr.T("{0} Bit je Pixel (Nummer in der Palette)", b);
            return i.Components > 1 ? Tr.T("{0} Bit je Kanal ({1} Bit je Pixel)", b, b * i.Components) : Tr.T("{0} Bit je Pixel", b);
        }

        string Orientation(int o) => o switch
        {
            3 => Tr.T("um 180° gedreht"),
            6 => Tr.T("um 90° im Uhrzeigersinn gedreht"),
            8 => Tr.T("um 90° gegen den Uhrzeigersinn gedreht"),
            _ => Tr.T("gespiegelt"),
        };
    }

    /// <summary>Text from the file (document information): cleaned like the texts in the image, with a longer limit.</summary>
    private static string? Clean(string? text) => ImageMetadata.Clean(text, 200);

    private static string? Join(string? make, string? model)
    {
        if (make is null) return model;
        if (model is null) return make;
        // many devices repeat the make in the model ("Make" / "Make Model 1")
        return model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model : make + " " + model;
    }

    private static string Bytes(long bytes, System.Globalization.CultureInfo c) => bytes switch
    {
        < 1024 => Tr.T("{0} Bytes", bytes),
        < 1024 * 1024 => Tr.T("{0} KB", (bytes / 1024.0).ToString("N0", c)),
        _ => Tr.T("{0} MB", (bytes / 1048576.0).ToString("N1", c)),
    };

    /// <summary>The sections as plain text (copying, command line).</summary>
    /// <param name="sections">The sections from <see cref="Sections"/>.</param>
    /// <returns>Headings and indented "label: value" lines, sections separated by a blank line.</returns>
    public static string AsText(IReadOnlyList<ImageInfoSection> sections)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var section in sections)
        {
            if (builder.Length > 0) builder.AppendLine();
            builder.AppendLine(section.Title);
            foreach (var (label, value) in section.Rows) builder.Append("  ").Append(label).Append(": ").AppendLine(value);
        }
        return builder.ToString();
    }
}
