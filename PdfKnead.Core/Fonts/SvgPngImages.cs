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
    private readonly double _scale;   // px per font unit

    private SvgPngImages(byte[] data, int list, int entries, int end, int ppem, int unitsPerEm)
    {
        _data = data;
        _list = list;
        _entries = entries;
        _end = end;
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
        if (width < 1 || height < 1 || (long)width * height > PngDecoder.MaxPixels) return null;
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
        var at = _list + (long)(uint)t.U32(record + 4);
        var length = (uint)t.U32(record + 8);
        if (length == 0 || at + length > _end) return null;
        if (Text(_data.AsSpan((int)at, (int)length)) is not { } text) return null;
        // Anything besides one image (shapes, references, transforms, an own coordinate system) is not a plain PNG glyph
        if (Unsupported().IsMatch(text)) return null;
        var from = 0;
        if (t.U16(record) != t.U16(record + 2))
        {
            // A document for several glyphs: the image after the element with id="glyph<ID>"
            from = text.IndexOf($"id=\"glyph{glyph}\"", StringComparison.Ordinal);
            if (from < 0) from = text.IndexOf($"id='glyph{glyph}'", StringComparison.Ordinal);
            if (from < 0) return null;
        }
        var tag = text.IndexOf("<image", from, StringComparison.Ordinal);
        if (tag < 0) return null;
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

    [GeneratedRegex("""<(path|rect|circle|ellipse|line|polyline|polygon|text|use|clipPath|mask|filter|pattern)[\s/>]|\s(transform|viewBox|clip-path|mask|filter)\s*=""")]
    private static partial Regex Unsupported();

    [GeneratedRegex("""\s([A-Za-z_][\w:.-]*)\s*=\s*("([^"]*)"|'([^']*)')""")]
    private static partial Regex Attribute();
}
