using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Imaging;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Images from the 'SVG ' table (OpenType 1.9, "SVG — The SVG (Scalable Vector Graphics) Table") whose glyph documents consist of a
/// single embedded PNG image (<c>&lt;image href="data:image/png;base64,…"&gt;</c>) – the way colour fonts made from ink scans carry
/// images in a higher resolution than CBDT allows. Documents with vector content, transforms or a viewBox are not used; such glyphs
/// have no image here.
/// </summary>
/// <remarks>
/// Coordinates are in font units with the origin on the baseline and y pointing down (as the table defines). Images are scaled to at
/// most <see cref="MaxPixelsPerEm"/>. Compressed documents are unpacked up to <see cref="MaxDocumentBytes"/>.
/// </remarks>
internal sealed partial class SvgPngImages : IGlyphImages
{
    /// <summary>
    /// Highest resolution used: 256 px per em give more than 800 dpi for a signature 6 cm wide and keep the images of a name at about
    /// 5 MB (the cache budget of <see cref="ColorGlyphs"/>).
    /// </summary>
    internal const int MaxPixelsPerEm = 256;

    /// <summary>Largest glyph document after unpacking; larger ones are skipped.</summary>
    internal const int MaxDocumentBytes = 16 << 20;

    private const int RecordLength = 12;

    private readonly byte[] _data;
    private readonly int _list, _entries, _end;
    private readonly int _unitsPerEm;
    private readonly double _scale;   // px per font unit

    private SvgPngImages(byte[] data, int list, int entries, int end, int ppem, int unitsPerEm)
    {
        _data = data;
        _list = list;
        _entries = entries;
        _end = end;
        _unitsPerEm = unitsPerEm;
        PixelsPerEm = ppem;
        _scale = (double)ppem / unitsPerEm;
    }

    /// <inheritdoc/>
    public int PixelsPerEm { get; }

    /// <summary>Reads the document list and takes the resolution from the first documents with a PNG image.</summary>
    /// <param name="font">The font.</param>
    /// <returns>The source, or null if the font has no SVG table or none of its first documents is a single PNG image.</returns>
    public static SvgPngImages? TryCreate(OpenTypeFont font)
    {
        if (font.Table("SVG ") is not { Length: >= 10 } table) return null;
        var data = font.FileData;
        var t = new Layout.TableData(data);
        // version 0 only (OpenType SVG 35.2; No. 1149)
        if (t.U16(table.Offset) != 0) return null;
        var end = table.Offset + table.Length;
        var list = table.Offset + (long)(uint)t.U32(table.Offset + 2);
        if (list + 2 > end) return null;
        var entries = (int)Math.Min(t.U16((int)list), (end - list - 2) / RecordLength);
        var probe = new SvgPngImages(data, (int)list, entries, end, MaxPixelsPerEm, font.UnitsPerEm);
        for (var i = 0; i < Math.Min(entries, 8); i++)
        {
            if (probe.Picture(i, t.U16((int)list + 2 + i * RecordLength)) is not { } picture) continue;
            var png = picture.Png.Span;
            if (png.Length < 24 || !PngDecoder.IsPng(png)) continue;
            // Width from the PNG header (IHDR) – no need to decode the image for its resolution
            var native = BinaryPrimitives.ReadInt32BigEndian(png[16..]) * (double)font.UnitsPerEm / picture.Width;
            if (!(native >= 1)) continue;
            return new SvgPngImages(data, (int)list, entries, end, (int)Math.Min(MaxPixelsPerEm, Math.Round(native)), font.UnitsPerEm);
        }
        return null;
    }

    /// <inheritdoc/>
    public (int X, int Y, RgbaImage Image)? Load(int glyph)
    {
        var t = new Layout.TableData(_data);
        // The records are sorted by glyph ID (required by the table)
        int low = 0, high = _entries - 1;
        while (low <= high)
        {
            var mid = (low + high) >>> 1;
            var record = _list + 2 + mid * RecordLength;
            if (glyph < t.U16(record)) high = mid - 1;
            else if (glyph > t.U16(record + 2)) low = mid + 1;
            else return Decode(mid, glyph);
        }
        return null;
    }

    private (int X, int Y, RgbaImage Image)? Decode(int entry, int glyph)
    {
        if (Picture(entry, glyph) is not { } picture) return null;
        var width = (int)Math.Round(picture.Width * _scale);
        var height = (int)Math.Round(picture.Height * _scale);
        if (width < 1 || height < 1 || (long)width * height > ColorGlyphs.MaxGlyphPixels || !ColorGlyphs.Fits(picture.Png.Span)) return null;
        try
        {
            var image = PngDecoder.Decode(picture.Png.Span);
            if (image.Width != width || image.Height != height) image = Lanczos.Resize(image, width, height);
            return ((int)Math.Round(picture.X * _scale), (int)Math.Round(-(picture.Y + picture.Height) * _scale), image);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>Reads the image element of a glyph: position and size in font units and the PNG data.</summary>
    private (double X, double Y, double Width, double Height, ReadOnlyMemory<byte> Png)? Picture(int entry, int glyph)
    {
        var t = new Layout.TableData(_data);
        var record = _list + 2 + entry * RecordLength;
        // an svgDocOffset of 0 names no document (35.2; before the document list itself was read as one, No. 1152)
        if ((uint)t.U32(record + 4) == 0) return null;
        var at = _list + (long)(uint)t.U32(record + 4);
        var length = (uint)t.U32(record + 8);
        if (length == 0 || at + length > _end) return null;
        if (Document(at, length) is not { } text) return null;
        // Anything besides one image (shapes, references, transforms, an own coordinate system) is not a plain PNG glyph
        if (Unsupported().IsMatch(text)) return null;
        // the element of the glyph, id="glyph<ID>", in every document (35.5; before only in one for several glyphs): its content only — an
        // empty element has no image; without it a document of this glyph alone is the glyph (No. 1152)
        var (from, to) = (0, text.Length);
        if (Element(text, glyph) is { } element) (from, to) = element;
        else if (t.U16(record) != t.U16(record + 2) || ElementOfAnother(text)) return null;
        if (from >= to) return null;
        // the first image of it that is not inside a link or a switch (only static content counts, 35.3.3)
        var tag = from;
        while (true)
        {
            tag = text.IndexOf("<image", tag, StringComparison.Ordinal);
            if (tag < 0 || tag >= to) return null;
            if (tag + 6 < text.Length && (char.IsWhiteSpace(text[tag + 6]) || text[tag + 6] is '/' or '>') && Depth(text, "a", tag) == 0 &&
                Depth(text, "switch", tag) == 0)
                break;
            tag += 6;
        }
        var close = text.IndexOf('>', tag);
        if (close < 0) return null;
        double x = 0, y = 0, w = 0, h = 0;
        string? href = null;
        foreach (Match m in Attribute().Matches(text[(tag + 6)..close]))
        {
            var value = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            switch (m.Groups[1].Value)
            {
                case "x": x = Number(value); break;
                case "y": y = Number(value); break;
                case "width": w = Number(value); break;
                case "height": h = Number(value); break;
                case "href" or "xlink:href": href = value; break;
            }
        }
        // width and height on the <svg> element scale the em square they stand for (35.7; before passed over, No. 1152)
        if (RootSize(text) is var (rootWidth, rootHeight))
        {
            var (sx, sy) = (rootWidth > 0 ? _unitsPerEm / rootWidth : 1, rootHeight > 0 ? _unitsPerEm / rootHeight : 1);
            (x, w, y, h) = (x * sx, w * sx, y * sy, h * sy);
        }
        const string prefix = "data:image/png;base64,";
        if (!(w > 0 && h > 0) || !double.IsFinite(x) || !double.IsFinite(y) || href is null || !href.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        try
        {
            return (x, y, w, h, Convert.FromBase64String(href[prefix.Length..]));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The document at <paramref name="at"/> as text, the last one kept: the records of a document shared by many glyphs name the same
    /// one, and each glyph unpacked it again (up to 16 MB) — some hundred megabytes for one name (No. 1138).
    /// </summary>
    private string? Document(long at, uint length)
    {
        lock (_lock)
        {
            if (_document is { } kept && kept.At == at && kept.Length == length) return kept.Text;
        }
        // without comments (and CDATA sections, which hold no elements): an image in a comment is none (35.3.2; No. 1152)
        var text = Text(_data.AsSpan((int)at, (int)length)) is { } raw ? Comments().Replace(raw, " ") : null;
        lock (_lock) _document = (at, length, text);
        return text;
    }

    private readonly Lock _lock = new();
    private (long At, uint Length, string? Text)? _document;

    /// <summary>Returns the document as text; gzip-compressed documents are unpacked up to <see cref="MaxDocumentBytes"/>.</summary>
    private static string? Text(ReadOnlySpan<byte> document)
    {
        if (document.Length < 2 || document[0] != 0x1F || document[1] != 0x8B)
            return document.Length <= MaxDocumentBytes ? Encoding.UTF8.GetString(document) : null;
        try
        {
            using var gzip = new GZipStream(new MemoryStream(document.ToArray()), CompressionMode.Decompress);
            var buffer = new byte[64 << 10];
            using var text = new MemoryStream();
            int read;
            while ((read = gzip.Read(buffer)) > 0)
            {
                if (text.Length + read > MaxDocumentBytes) return null;
                text.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static double Number(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;

    /// <summary>
    /// The content of the element with id="glyph<paramref name="glyph"/>": from behind its start tag to its end tag (an empty element gives
    /// an empty range); null if the document has no such element.
    /// </summary>
    private static (int From, int To)? Element(string text, int glyph)
    {
        var id = text.IndexOf($"id=\"glyph{glyph}\"", StringComparison.Ordinal);
        if (id < 0) id = text.IndexOf($"id='glyph{glyph}'", StringComparison.Ordinal);
        if (id < 0) return null;
        var open = text.LastIndexOf('<', id);
        var close = text.IndexOf('>', id);
        if (open < 0 || close < 0) return (0, 0);
        if (text[close - 1] == '/') return (close + 1, close + 1);
        var nameEnd = open + 1;
        while (nameEnd < text.Length && !char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] is not ('>' or '/')) nameEnd++;
        var name = text[(open + 1)..nameEnd];
        // its end tag, counting elements of the same name inside it
        var depth = 1;
        for (var at = close + 1; at < text.Length;)
        {
            var next = text.IndexOf('<', at);
            if (next < 0) break;
            if (text.AsSpan(next + 1).StartsWith("/" + name) && Ends(text, next + 2 + name.Length) && --depth == 0) return (close + 1, next);
            if (text.AsSpan(next + 1).StartsWith(name) && Ends(text, next + 1 + name.Length))
            {
                var end = text.IndexOf('>', next);
                if (end > 0 && text[end - 1] != '/') depth++;
            }
            at = next + 1;
        }
        return (close + 1, text.Length);
    }

    /// <summary>Whether the document has glyph elements (id="glyph…") — then a glyph without its own has none.</summary>
    private static bool ElementOfAnother(string text) => text.Contains("id=\"glyph", StringComparison.Ordinal) || text.Contains("id='glyph", StringComparison.Ordinal);

    private static bool Ends(string text, int at) => at >= text.Length || char.IsWhiteSpace(text[at]) || text[at] is '>' or '/';

    /// <summary>How many elements named <paramref name="name"/> are open at <paramref name="position"/> (self-closing ones not counted).</summary>
    private static int Depth(string text, string name, int position)
    {
        var depth = 0;
        for (var at = text.IndexOf('<'); at >= 0 && at < position; at = text.IndexOf('<', at + 1))
        {
            if (text.AsSpan(at + 1).StartsWith("/" + name) && Ends(text, at + 2 + name.Length)) depth = Math.Max(0, depth - 1);
            else if (text.AsSpan(at + 1).StartsWith(name) && Ends(text, at + 1 + name.Length))
            {
                var end = text.IndexOf('>', at);
                if (end > 0 && text[end - 1] != '/') depth++;
            }
        }
        return depth;
    }

    /// <summary>The plain numbers of width and height of the &lt;svg&gt; element, null where it has neither.</summary>
    private static (double Width, double Height)? RootSize(string text)
    {
        var root = text.IndexOf("<svg", StringComparison.Ordinal);
        if (root < 0) return null;
        var close = text.IndexOf('>', root);
        if (close < 0) return null;
        double width = 0, height = 0;
        foreach (Match m in Attribute().Matches(text[(root + 4)..close]))
        {
            var value = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            if (m.Groups[1].Value == "width") width = Number(value);
            else if (m.Groups[1].Value == "height") height = Number(value);
        }
        width = double.IsFinite(width) ? width : 0;
        height = double.IsFinite(height) ? height : 0;
        return width > 0 || height > 0 ? (width, height) : null;
    }

    [GeneratedRegex("""<(path|rect|circle|ellipse|line|polyline|polygon|text|use|clipPath|mask|filter|pattern)[\s/>]|\s(transform|viewBox|clip-path|mask|filter)\s*=""")]
    private static partial Regex Unsupported();

    [GeneratedRegex("""<!--.*?-->|<!\[CDATA\[.*?\]\]>""", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex("""\s([A-Za-z_][\w:.-]*)\s*=\s*("([^"]*)"|'([^']*)')""")]
    private static partial Regex Attribute();
}
