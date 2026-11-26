using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Graphics;
using SkiaSharp;

namespace PdfKnead.Tests.Fonts;

/// <summary>
/// Builds colour fonts for tests: a free handwriting font whose letters are drawn as ink images (blue, anti-aliased, transparent
/// background) and added as an 'sbix', 'CBDT'/'CBLC' or 'SVG ' table. The outlines stay, so the fonts parse like any other.
/// </summary>
public static class ColorFontBuilder
{
    /// <summary>Table formats the builder writes.</summary>
    public enum Kind
    {
        /// <summary>'sbix' with one strike.</summary>
        Sbix,
        /// <summary>'CBDT'/'CBLC', index subtables format 1 (one per run of glyph IDs), image format 17.</summary>
        Cbdt,
        /// <summary>'CBDT'/'CBLC', index subtable format 4 (glyph ID list), image format 18.</summary>
        CbdtSparse,
        /// <summary>'SVG ' with one gzip-compressed document per glyph.</summary>
        Svg,
        /// <summary>'SVG ' with one uncompressed document for all glyphs.</summary>
        SvgShared,
    }

    /// <summary>Ink colour of the images.</summary>
    internal static readonly SKColor Ink = new(0x10, 0x20, 0x90);

    /// <summary>The letters that get images (Latin letters, digits, German umlauts and a few punctuation marks).</summary>
    internal const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyzÄÖÜäöüß0123456789 .,-";

    /// <summary>Builds a colour font.</summary>
    /// <param name="kind">Table format.</param>
    /// <param name="pixelsPerEm">Resolution of the images.</param>
    /// <returns>The font file.</returns>
    public static byte[] Build(Kind kind, int pixelsPerEm)
    {
        var source = EmbeddedFonts.LoadHandwriting("PatrickHand-Regular.ttf");
        var font = OpenTypeFont.Parse(source);
        var images = Images(font, pixelsPerEm);
        var tables = kind switch
        {
            Kind.Sbix => new Dictionary<string, byte[]?> { ["sbix"] = Sbix(images, font, pixelsPerEm) },
            Kind.Cbdt or Kind.CbdtSparse => Cbdt(images, pixelsPerEm, kind == Kind.CbdtSparse),
            _ => new Dictionary<string, byte[]?> { ["SVG "] = Svg(images, font.UnitsPerEm, pixelsPerEm, kind == Kind.SvgShared) },
        };
        return WithTables(source, tables);
    }

    /// <summary>One glyph image: lower left corner in px relative to the glyph origin (y upwards) and the PNG.</summary>
    internal sealed record Image(int Glyph, int Left, int Bottom, int Width, int Height, byte[] Png);

    /// <summary>Draws the outlines of <see cref="Letters"/> as ink images.</summary>
    internal static List<Image> Images(OpenTypeFont font, int pixelsPerEm)
    {
        var scale = (double)pixelsPerEm / font.UnitsPerEm;
        var result = new Dictionary<int, Image>();
        foreach (var c in Letters)
        {
            var glyph = font.GlyphForUnicode(c);
            var outline = font.Outline(glyph);
            if (glyph <= 0 || outline.IsEmpty || result.ContainsKey(glyph)) continue;
            var b = outline.Bounds;
            var left = (int)Math.Floor(b.Left * scale) - 1;
            var bottom = (int)Math.Floor(b.Top * scale) - 1;
            var width = (int)Math.Ceiling(b.Right * scale) + 1 - left;
            var height = (int)Math.Ceiling(b.Bottom * scale) + 1 - bottom;
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            using (var canvas = new SKCanvas(bitmap))
            using (var path = Path(outline, scale, left, bottom + height))
            using (var paint = new SKPaint { IsAntialias = true, Color = Ink })
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawPath(path, paint);
            }
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            result[glyph] = new Image(glyph, left, bottom, width, height, png.ToArray());
        }
        return [.. result.Values.OrderBy(i => i.Glyph)];
    }

    private static SKPath Path(GlyphOutline outline, double scale, int left, int top)
    {
        var path = new SKPath();
        var p = outline.Points;
        var i = 0;
        float X(int k) => (float)(p[k] * scale - left);
        float Y(int k) => (float)(top - p[k + 1] * scale);
        foreach (var verb in outline.Verbs)
            switch (verb)
            {
                case PathVerb.Move: path.MoveTo(X(i), Y(i)); i += 2; break;
                case PathVerb.Line: path.LineTo(X(i), Y(i)); i += 2; break;
                case PathVerb.Cubic: path.CubicTo(X(i), Y(i), X(i + 2), Y(i + 2), X(i + 4), Y(i + 4)); i += 6; break;
                case PathVerb.Close: path.Close(); break;
            }
        return path;
    }

    // ------------------------------------------------------------ Tables

    private static byte[] Sbix(List<Image> images, OpenTypeFont font, int ppem)
    {
        var glyphCount = font.GlyphCount;
        var byGlyph = images.ToDictionary(i => i.Glyph);
        var k = (double)ppem / font.UnitsPerEm;
        var strike = new Writer();
        strike.U16(ppem);
        strike.U16(72);
        var data = new Writer();
        var offsets = new List<int>();
        var header = 4 + 4 * (glyphCount + 1);
        for (var g = 0; g < glyphCount; g++)
        {
            offsets.Add(header + data.Length);
            if (!byGlyph.TryGetValue(g, out var image)) continue;
            // the origin offset counts from the lower left corner of the glyph box for a glyph with contours (OpenType 34.3), from the pen otherwise
            var (xMin, yMin) = font.ContourCorner(g) ?? (0, 0);
            data.I16(image.Left - (int)Math.Round(xMin * k));
            data.I16(image.Bottom - (int)Math.Round(yMin * k));
            data.Bytes("png "u8);
            data.Bytes(image.Png);
        }
        offsets.Add(header + data.Length);
        foreach (var o in offsets) strike.U32(o);
        strike.Bytes(data.ToArray());
        var table = new Writer();
        table.U16(1);    // version
        table.U16(1);    // flags: draw the images only
        table.U32(1);    // one strike
        table.U32(12);
        table.Bytes(strike.ToArray());
        return table.ToArray();
    }

    private static Dictionary<string, byte[]?> Cbdt(List<Image> images, int ppem, bool sparse)
    {
        var data = new Writer();
        data.U16(3);
        data.U16(0);
        var at = new Dictionary<int, (int Offset, int Length)>();
        foreach (var image in images)
        {
            var start = data.Length;
            data.U8(image.Height);
            data.U8(image.Width);
            data.I8(image.Left);
            data.I8(image.Bottom + image.Height);   // bearing: top edge above the baseline
            data.U8(ppem / 2);
            if (sparse)
            {
                data.I8(0);
                data.I8(0);
                data.U8(ppem);
            }
            data.U32(image.Png.Length);
            data.Bytes(image.Png);
            at[image.Glyph] = (start, data.Length - start);
        }

        // Runs of consecutive glyph IDs (format 1), or one list of all glyphs (format 4)
        var runs = new List<List<Image>>();
        foreach (var image in images)
            if (!sparse && runs.Count > 0 && runs[^1][^1].Glyph == image.Glyph - 1) runs[^1].Add(image);
            else if (sparse && runs.Count > 0) runs[0].Add(image);
            else runs.Add([image]);
        var subTables = new Writer();
        var subOffsets = new List<int>();
        foreach (var run in runs)
        {
            subOffsets.Add(8 * runs.Count + subTables.Length);
            var first = at[run[0].Glyph].Offset;
            subTables.U16(sparse ? 4 : 1);
            subTables.U16(sparse ? 18 : 17);
            subTables.U32(first);
            if (sparse)
            {
                subTables.U32(run.Count);
                foreach (var image in run)
                {
                    subTables.U16(image.Glyph);
                    subTables.U16(at[image.Glyph].Offset - first);
                }
                subTables.U16(0);
                subTables.U16(at[run[^1].Glyph].Offset + at[run[^1].Glyph].Length - first);
            }
            else
            {
                foreach (var image in run) subTables.U32(at[image.Glyph].Offset - first);
                subTables.U32(at[run[^1].Glyph].Offset + at[run[^1].Glyph].Length - first);
            }
            while (subTables.Length % 4 != 0) subTables.U8(0);
        }
        var array = new Writer();
        for (var i = 0; i < runs.Count; i++)
        {
            array.U16(runs[i][0].Glyph);
            array.U16(runs[i][^1].Glyph);
            array.U32(subOffsets[i]);
        }
        array.Bytes(subTables.ToArray());

        var location = new Writer();
        location.U16(3);
        location.U16(0);
        location.U32(1);
        location.U32(8 + 48);              // indexSubTableArrayOffset
        location.U32(array.Length);
        location.U32(runs.Count);
        location.U32(0);                   // colorRef
        for (var m = 0; m < 2; m++)        // horizontal and vertical line metrics
        {
            location.I8(ppem * 8 / 10);
            location.I8(-ppem * 2 / 10);
            location.Bytes(new byte[10]);
        }
        location.U16(images[0].Glyph);
        location.U16(images[^1].Glyph);
        location.U8(ppem);
        location.U8(ppem);
        location.U8(32);                   // bit depth
        location.U8(1);                    // horizontal metrics
        location.Bytes(array.ToArray());
        return new Dictionary<string, byte[]?> { ["CBDT"] = data.ToArray(), ["CBLC"] = location.ToArray() };
    }

    private static byte[] Svg(List<Image> images, int unitsPerEm, int ppem, bool shared)
    {
        var units = (double)unitsPerEm / ppem;
        string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        string Element(Image i) =>
            $"""<g id="glyph{i.Glyph}"><image x="{N(i.Left * units)}" y="{N(-(i.Bottom + i.Height) * units)}" width="{N(i.Width * units)}" height="{N(i.Height * units)}" preserveAspectRatio="none" xlink:href="data:image/png;base64,{Convert.ToBase64String(i.Png)}"/></g>""";
        const string open = """<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" version="1.1">""";
        List<(int First, int Last, byte[] Data)> documents = shared
            ? [(images[0].Glyph, images[^1].Glyph, Encoding.UTF8.GetBytes(open + string.Concat(images.Select(Element)) + "</svg>"))]
            : images.Select(i => (i.Glyph, i.Glyph, Gzip(Encoding.UTF8.GetBytes(open + Element(i) + "</svg>")))).ToList();
        var table = new Writer();
        table.U16(0);
        table.U32(10);
        table.U32(0);
        table.U16(documents.Count);
        var offset = 2 + 12 * documents.Count;
        foreach (var (first, last, data) in documents)
        {
            table.U16(first);
            table.U16(last);
            table.U32(offset);
            table.U32(data.Length);
            offset += data.Length;
        }
        foreach (var (_, _, data) in documents) table.Bytes(data);
        return table.ToArray();
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest)) gzip.Write(data);
        return output.ToArray();
    }

    /// <summary>Writes the font again with additional or replaced tables; null removes a table (directory sorted by tag, tables aligned to 4 bytes).</summary>
    internal static byte[] WithTables(byte[] font, IReadOnlyDictionary<string, byte[]?> changed)
    {
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (var i = 0; i < count; i++)
        {
            var record = font.AsSpan(12 + 16 * i);
            var tag = Encoding.ASCII.GetString(record[..4]);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            tables[tag] = font.AsSpan(offset, length).ToArray();
        }
        foreach (var (tag, data) in changed)
            if (data is null) tables.Remove(tag);
            else tables[tag] = data;
        var w = new Writer();
        w.Bytes(font.AsSpan(0, 4));
        var log = (int)Math.Log2(tables.Count);
        w.U16(tables.Count);
        w.U16(16 << log);
        w.U16(log);
        w.U16(tables.Count * 16 - (16 << log));
        var at = 12 + 16 * tables.Count;
        foreach (var (tag, data) in tables)
        {
            w.Bytes(Encoding.ASCII.GetBytes(tag));
            w.U32((int)Checksum(data));
            w.U32(at);
            w.U32(data.Length);
            at += (data.Length + 3) & ~3;
        }
        foreach (var data in tables.Values)
        {
            w.Bytes(data);
            while (w.Length % 4 != 0) w.U8(0);
        }
        return w.ToArray();
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint v = 0;
            for (var k = 0; k < 4; k++) v = (v << 8) | (i + k < data.Length ? data[i + k] : 0u);
            sum += v;
        }
        return sum;
    }

    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();

        public int Length => (int)_stream.Length;

        public void U8(int v) => _stream.WriteByte((byte)v);

        public void I8(int v) => _stream.WriteByte((byte)(sbyte)v);

        public void U16(int v)
        {
            U8(v >> 8);
            U8(v);
        }

        public void I16(int v) => U16((ushort)(short)v);

        public void U32(int v)
        {
            U16(v >> 16);
            U16(v);
        }

        public void Bytes(ReadOnlySpan<byte> data) => _stream.Write(data);

        public byte[] ToArray() => _stream.ToArray();
    }
}

/// <summary>
/// The generated colour fonts in the user font folder of the tests (<see cref="SignatureFontTests"/>): usable as signature fonts by
/// file name like any font a user imports.
/// </summary>
internal static class TestColorFonts
{
    /// <summary>Colour font with an 'sbix' table (128 px per em).</summary>
    public const string Sbix = "Testfarbschrift-sbix.ttf";

    /// <summary>Colour font with 'CBDT'/'CBLC' tables (96 px per em).</summary>
    public const string Cbdt = "Testfarbschrift-cbdt.ttf";

    /// <summary>Colour font with an 'SVG ' table of PNG images (320 px per em, used at 256).</summary>
    public const string Svg = "Testfarbschrift-svg.ttf";

    /// <summary>All three file names.</summary>
    public static readonly IReadOnlyList<string> All = [Sbix, Cbdt, Svg];

    /// <summary>
    /// Writes the three fonts into a folder, each first under a temporary name and then renamed in one step: every test process
    /// writes them anew into the same folder, and another one reading at that moment must never see a half-written font (it would
    /// leave it out of the list).
    /// </summary>
    /// <param name="folder">The folder, created if necessary.</param>
    public static void WriteTo(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var (file, kind, ppem) in new[] { (Sbix, ColorFontBuilder.Kind.Sbix, 128), (Cbdt, ColorFontBuilder.Kind.Cbdt, 96), (Svg, ColorFontBuilder.Kind.Svg, 320) })
        {
            var target = Path.Combine(folder, file);
            var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temporary, ColorFontBuilder.Build(kind, ppem));
            File.Move(temporary, target, overwrite: true);
        }
    }
}
