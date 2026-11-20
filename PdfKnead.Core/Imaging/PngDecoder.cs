using System.Buffers.Binary;
using System.IO.Compression;

namespace PdfKnead.Core.Imaging;

/// <summary>Image with 8 bits per channel, RGBA, not premultiplied, rows from the top.</summary>
/// <param name="width">Width in px.</param>
/// <param name="height">Height in px.</param>
/// <param name="pixels">The pixels, <paramref name="width"/> × <paramref name="height"/> × 4 bytes; used directly, not copied.</param>
public sealed class RgbaImage(int width, int height, byte[] pixels)
{
    /// <summary>Width in px.</summary>
    public int Width { get; } = width;

    /// <summary>Height in px.</summary>
    public int Height { get; } = height;

    /// <summary>R, G, B, A per pixel.</summary>
    public byte[] Pixels { get; } = pixels;

    /// <summary>Creates a fully transparent image.</summary>
    /// <param name="width">Width in px.</param>
    /// <param name="height">Height in px.</param>
    /// <returns>The new image.</returns>
    /// <exception cref="OverflowException">The byte size does not fit into an int.</exception>
    public static RgbaImage Blank(int width, int height) => new(width, height, new byte[checked(width * height * 4)]);

    /// <summary>
    /// Composites <paramref name="source"/> onto this image (source over destination, with alpha), with its upper left corner at
    /// (<paramref name="left"/>, <paramref name="top"/>); whatever sticks out is clipped.
    /// </summary>
    /// <param name="source">The image to draw.</param>
    /// <param name="left">X of the upper left corner in px.</param>
    /// <param name="top">Y of the upper left corner in px.</param>
    public void Draw(RgbaImage source, int left, int top)
    {
        for (var sy = 0; sy < source.Height; sy++)
        {
            var y = top + sy;
            if (y < 0 || y >= Height) continue;
            for (var sx = 0; sx < source.Width; sx++)
            {
                var x = left + sx;
                if (x < 0 || x >= Width) continue;
                var s = (sy * source.Width + sx) * 4;
                var sa = source.Pixels[s + 3];
                if (sa == 0) continue;
                var d = (y * Width + x) * 4;
                if (sa == 255 || Pixels[d + 3] == 0)
                {
                    Array.Copy(source.Pixels, s, Pixels, d, 4);
                    continue;
                }
                // Source over destination, not premultiplied
                double a = sa / 255.0, b = Pixels[d + 3] / 255.0 * (1 - a), outA = a + b;
                for (var k = 0; k < 3; k++)
                    Pixels[d + k] = (byte)Math.Round((source.Pixels[s + k] * a + Pixels[d + k] * b) / outA);
                Pixels[d + 3] = (byte)Math.Round(outA * 255);
            }
        }
    }
}

/// <summary>
/// Reads PNG (ISO/IEC 15948, RFC 2083): all colour types, 1–16 bits, palette with tRNS, row filters 0–4, Adam7 interlacing. Used for colour
/// glyphs (sbix, CBDT, SVG) and images that serve as signatures. What RFC 2083 asks of a decoder is checked and refused with an error: an
/// unknown critical chunk, a method field it does not know, a colour type 3 without palette, an unknown row filter, a critical chunk with
/// a wrong CRC, IHDR not first, a second palette or one after the image data, image data not in one run (docs/FEHLERGEDAECHTNIS.md, No. 940).
/// Ancillary chunks it does not use, or with a wrong CRC or in a wrong place, are passed over (RFC 2083, 10.1).
/// </summary>
/// <remarks>16-bit samples are reduced to their upper byte; tRNS is applied to greyscale, RGB (8 and 16 bits, the key compared with the full
/// sample) and palette images.</remarks>
public static class PngDecoder
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Checks the PNG signature.</summary>
    /// <param name="data">The file data.</param>
    /// <returns>True if the data starts with the PNG signature and has more bytes after it.</returns>
    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length > 8 && data[..8].SequenceEqual(Signature);

    /// <summary>Decodes a PNG image.</summary>
    /// <param name="data">The file data.</param>
    /// <param name="complete">
    /// Whether the image data must cover the whole image (a scanner page): then data that unpack to less are damaged — refused before the
    /// image is allocated, a header naming a huge image over a few bytes of data takes no memory (docs/FEHLERGEDAECHTNIS.md, No. 965; as
    /// No. 958 for JPEG). Without it (glyphs of fonts, signatures) a truncated image shows what it has, as other readers show it.
    /// </param>
    /// <returns>The image; rows missing at the end of truncated data stay transparent.</returns>
    /// <exception cref="InvalidDataException">The data is not a PNG, the header is invalid, the image exceeds <see cref="MaxPixels"/>,
    /// there is no image data, or (<paramref name="complete"/>) less than the image needs.</exception>
    public static RgbaImage Decode(ReadOnlySpan<byte> data, bool complete = false)
    {
        if (!IsPng(data)) throw new InvalidDataException("kein PNG");
        int width = 0, height = 0, depth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null, transparency = null;
        bool header = false, inData = false, afterData = false;
        using var idat = new MemoryStream();
        for (var at = 8; at + 12 <= data.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            if (length < 0 || at + 12 + length > data.Length) break;   // cut short: what came before is read
            var type = data.Slice(at + 4, 4);
            var body = data.Slice(at + 8, length);
            at += 12 + length;
            // the type code by a literal comparison of four bytes, critical by bit 5 of its first byte (RFC 2083, 3.3)
            var critical = (type[0] & 0x20) == 0;
            if (!header && !type.SequenceEqual("IHDR"u8)) throw new InvalidDataException("PNG: IHDR nicht zuerst");
            var stored = BinaryPrimitives.ReadUInt32BigEndian(data[(at - 4)..]);
            if ((Crc32.Update(Crc32.Update(0xFFFFFFFF, type), body) ^ 0xFFFFFFFF) != stored)
            {
                if (critical) throw new InvalidDataException("PNG: Prüfsumme eines kritischen Chunks falsch");
                continue;   // an ancillary chunk with a wrong CRC is passed over (10.1)
            }
            if (inData && !type.SequenceEqual("IDAT"u8)) (inData, afterData) = (false, true);
            if (type.SequenceEqual("IHDR"u8))
            {
                // 13 bytes; compression and filter method 0, interlace method 0 or 1 (4.1.1, 10.1)
                if (header) throw new InvalidDataException("PNG: zweiter Kopf");
                if (body.Length != 13) throw new InvalidDataException("PNG-Kopf mit falscher Länge");
                if (body[10] != 0 || body[11] != 0 || body[12] > 1) throw new InvalidDataException("PNG: unbekanntes Verfahren im Kopf");
                header = true;
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                depth = body[8];
                colorType = body[9];
                interlace = body[12];
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                // one, before the image data, a whole number of entries, at most 256 (4.1.2)
                if (palette is not null || inData || afterData || length == 0 || length % 3 != 0 || length > 768)
                    throw new InvalidDataException("PNG: Palette ungültig");
                palette = body.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                if (!inData && !afterData) transparency = body.ToArray();   // after the image data it is in a wrong place: passed over
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (afterData) throw new InvalidDataException("PNG: Bilddaten nicht zusammenhängend");
                inData = true;
                idat.Write(body);
            }
            else if (type.SequenceEqual("IEND"u8)) break;
            else if (critical) throw new InvalidDataException("PNG: unbekannter kritischer Chunk");   // 3.3: cannot be shown safely
        }
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels) throw new InvalidDataException("PNG-Größe ungültig");
        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException("PNG-Farbart") };
        if (colorType == 3 && palette is null) throw new InvalidDataException("PNG: Palette fehlt");
        // Allowed bit depths per colour type (ISO/IEC 15948, 11.2.2); any other (such as 0) would later divide by zero
        var depthOk = colorType switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            _ => depth is 8 or 16,
        };
        if (!depthOk) throw new InvalidDataException("PNG-Bittiefe");
        var bitsPerPixel = channels * depth;
        var bytesPerPixel = Math.Max(1, bitsPerPixel / 8);

        // Inflate at most up to the size the image needs (against decompression bombs), and allocate the image
        // only afterwards, not for a claimed huge size without data
        idat.Position = 0;
        var expected = RawSize(width, height, bitsPerPixel, interlace != 0);
        using var inflated = new MemoryStream((int)Math.Min(expected, 1 << 20));
        try
        {
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            var buffer = new byte[81920];
            int n;
            while (inflated.Length < expected && (n = z.Read(buffer, 0, (int)Math.Min(buffer.Length, expected - inflated.Length))) > 0)
                inflated.Write(buffer, 0, n);
        }
        catch (IOException e)
        {
            // damaged compressed data: the inflater throws its own exception (an IOException) — callers know only InvalidDataException
            // (Fehlergedächtnis 958)
            throw new InvalidDataException("PNG-Daten beschädigt", e);
        }
        if (inflated.Length == 0) throw new InvalidDataException("PNG ohne Bilddaten");
        if (complete && inflated.Length < expected) throw new InvalidDataException("PNG: weniger Bilddaten, als das Bild braucht");
        var raw = inflated.GetBuffer().AsSpan(0, (int)inflated.Length);

        var image = RgbaImage.Blank(width, height);
        var offset = 0;
        if (interlace == 0)
        {
            Pass(raw, ref offset, image, 0, 0, 1, 1, width, height, depth, colorType, bitsPerPixel, bytesPerPixel, palette, transparency);
        }
        else
        {
            // Adam7: seven passes, each with its own grid
            foreach (var (x0, y0, dx, dy) in Adam7)
            {
                var w = (width - x0 + dx - 1) / dx;
                var h = (height - y0 + dy - 1) / dy;
                if (w > 0 && h > 0) Pass(raw, ref offset, image, x0, y0, dx, dy, w, h, depth, colorType, bitsPerPixel, bytesPerPixel, palette, transparency);
            }
        }
        return image;
    }

    /// <summary>Largest image: 2^25 pixels (about 33.5 million, 128 MB as RGBA); glyphs and signatures are much smaller.</summary>
    public const long MaxPixels = 1L << 25;

    private static readonly (int X, int Y, int Dx, int Dy)[] Adam7 = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];

    /// <summary>Bytes of the inflated image data (one filter byte per row, per pass when interlaced).</summary>
    private static long RawSize(int width, int height, int bitsPerPixel, bool interlaced)
    {
        static long Size(long w, long h, int bits) => w > 0 && h > 0 ? h * (1 + (w * bits + 7) / 8) : 0;
        if (!interlaced) return Size(width, height, bitsPerPixel);
        long total = 0;
        foreach (var (x0, y0, dx, dy) in Adam7) total += Size((width - x0 + dx - 1) / dx, (height - y0 + dy - 1) / dy, bitsPerPixel);
        return total;
    }

    private static void Pass(Span<byte> raw, ref int offset, RgbaImage image, int x0, int y0, int dx, int dy, int w, int h,
        int depth, int colorType, int bitsPerPixel, int bytesPerPixel, byte[]? palette, byte[]? transparency)
    {
        var stride = (w * bitsPerPixel + 7) / 8;
        var previous = new byte[stride];
        var line = new byte[stride];
        for (var row = 0; row < h; row++)
        {
            if (offset + 1 + stride > raw.Length) return;   // truncated: the rest stays transparent
            var filter = raw[offset];
            raw.Slice(offset + 1, stride).CopyTo(line);
            offset += 1 + stride;
            Unfilter(filter, line, previous, bytesPerPixel);
            var y = y0 + row * dy;
            for (var col = 0; col < w; col++)
                SetPixel(image, x0 + col * dx, y, line, col, depth, colorType, palette, transparency);
            (previous, line) = (line, previous);
        }
    }

    private static void Unfilter(byte filter, byte[] line, byte[] previous, int bpp)
    {
        switch (filter)
        {
            case 0:
                break;
            case > 4:
                throw new InvalidDataException("PNG: unbekannter Zeilenfilter");   // filter method 0 knows the types 0 to 4 (RFC 2083, 6)
            case 1:
                for (var i = bpp; i < line.Length; i++) line[i] += line[i - bpp];
                break;
            case 2:
                for (var i = 0; i < line.Length; i++) line[i] += previous[i];
                break;
            case 3:
                for (var i = 0; i < line.Length; i++) line[i] += (byte)(((i >= bpp ? line[i - bpp] : 0) + previous[i]) / 2);
                break;
            case 4:
                for (var i = 0; i < line.Length; i++)
                {
                    int a = i >= bpp ? line[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    line[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }
                break;
        }
    }

    /// <summary>Sample number <paramref name="index"/> of the row at <paramref name="depth"/> bits, scaled to 8 bits (raw: the unscaled value, e.g. a palette index).</summary>
    private static int Sample(byte[] line, int index, int depth, bool raw)
    {
        switch (depth)
        {
            case 8:
                return line[index];
            case 16:
                return line[index * 2];
            default:
                var bit = index * depth;
                var value = (line[bit >> 3] >> (8 - depth - (bit & 7))) & ((1 << depth) - 1);
                return raw ? value : value * 255 / ((1 << depth) - 1);
        }
    }

    /// <summary>Sample number <paramref name="index"/> unscaled, at 16 bits with both bytes (for comparing with the tRNS key).</summary>
    private static int Exact(byte[] line, int index, int depth) => depth == 16 ? line[index * 2] << 8 | line[index * 2 + 1] : Sample(line, index, depth, raw: true);

    /// <summary>A two-byte value of the tRNS chunk (big-endian).</summary>
    private static int Key(byte[] transparency, int at) => transparency[at] << 8 | transparency[at + 1];

    private static void SetPixel(RgbaImage image, int x, int y, byte[] line, int col, int depth, int colorType, byte[]? palette, byte[]? transparency)
    {
        if (x >= image.Width || y >= image.Height) return;
        int r, g, b, a = 255;
        switch (colorType)
        {
            case 0:
                r = g = b = Sample(line, col, depth, raw: false);
                // the key (ISO/IEC 15948, 11.3.2.1) is compared with the whole sample, at 16 bits with both bytes
                if (transparency is { Length: >= 2 } && Exact(line, col, depth) == (Key(transparency, 0) & ((1 << depth) - 1))) a = 0;
                break;
            case 2:
                r = Sample(line, col * 3, depth, false);
                g = Sample(line, col * 3 + 1, depth, false);
                b = Sample(line, col * 3 + 2, depth, false);
                if (transparency is { Length: >= 6 } && Exact(line, col * 3, depth) == Key(transparency, 0) &&
                    Exact(line, col * 3 + 1, depth) == Key(transparency, 2) && Exact(line, col * 3 + 2, depth) == Key(transparency, 4))
                    a = 0;
                break;
            case 3:
                var index = Sample(line, col, depth, raw: true);
                if (palette is null || index * 3 + 2 >= palette.Length) return;
                r = palette[index * 3];
                g = palette[index * 3 + 1];
                b = palette[index * 3 + 2];
                if (transparency is not null && index < transparency.Length) a = transparency[index];
                break;
            case 4:
                r = g = b = Sample(line, col * 2, depth, false);
                a = Sample(line, col * 2 + 1, depth, false);
                break;
            default:
                r = Sample(line, col * 4, depth, false);
                g = Sample(line, col * 4 + 1, depth, false);
                b = Sample(line, col * 4 + 2, depth, false);
                a = Sample(line, col * 4 + 3, depth, false);
                break;
        }
        var d = (y * image.Width + x) * 4;
        image.Pixels[d] = (byte)r;
        image.Pixels[d + 1] = (byte)g;
        image.Pixels[d + 2] = (byte)b;
        image.Pixels[d + 3] = (byte)a;
    }
}
