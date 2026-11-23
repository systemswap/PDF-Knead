using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace PdfKnead.Core.Imaging;

/// <summary>Colour type of a PNG image (ISO/IEC 15948, 11.2.2).</summary>
public enum PngColorType : byte
{
    /// <summary>Greyscale, 1, 2, 4, 8 or 16 bits.</summary>
    Gray = 0,

    /// <summary>RGB, 8 or 16 bits per channel.</summary>
    Rgb = 2,

    /// <summary>Palette indices, 1, 2, 4 or 8 bits.</summary>
    Palette = 3,

    /// <summary>Greyscale with alpha, 8 or 16 bits per channel.</summary>
    GrayAlpha = 4,

    /// <summary>RGB with alpha, 8 or 16 bits per channel.</summary>
    Rgba = 6,
}

/// <summary>
/// Writes PNG (ISO/IEC 15948) losslessly in every bit depth and colour type, with a palette and an ICC profile if given. Each row
/// gets the filter that makes its bytes smallest in sum (the usual heuristic, 12.8), then all rows are compressed together.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Encodes an image.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="bitDepth">Bits per channel (palette: per index).</param>
    /// <param name="type">Colour type.</param>
    /// <param name="row">Writes row <c>y</c> (from the top) into the span: packed as in PNG, 16-bit values big-endian.</param>
    /// <param name="output">Where the file goes.</param>
    /// <param name="palette">For <see cref="PngColorType.Palette"/>: RGB triples (at most 256).</param>
    /// <param name="paletteAlpha">For <see cref="PngColorType.Palette"/>: opacity of the first entries (tRNS), or null for opaque.</param>
    /// <param name="iccProfile">An ICC profile to embed (iCCP), or null; it must describe the colour type (RGB or grey).</param>
    /// <param name="dpi">The resolution to note in the file (pHYs, in pixels per metre), or 0 for none.</param>
    /// <param name="level">How hard to compress: the smallest file by default, <see cref="CompressionLevel.Optimal"/> for large pages.</param>
    /// <param name="significantBits">The original number of bits of each channel when the data were scaled up from a smaller depth (sBIT,
    /// RFC 2083, 4.2.6: one value per channel, the palette channels for colour type 3), or null.</param>
    /// <exception cref="ArgumentException">Bit depth and colour type do not fit together, or the palette is missing.</exception>
    public static void Encode(int width, int height, int bitDepth, PngColorType type, Action<int, Span<byte>> row, Stream output,
        byte[]? palette = null, byte[]? paletteAlpha = null, byte[]? iccProfile = null, double dpi = 0,
        CompressionLevel level = CompressionLevel.SmallestSize, byte[]? significantBits = null)
    {
        var channels = type switch { PngColorType.Gray or PngColorType.Palette => 1, PngColorType.GrayAlpha => 2, PngColorType.Rgb => 3, _ => 4 };
        var allowed = type switch
        {
            PngColorType.Gray => bitDepth is 1 or 2 or 4 or 8 or 16,
            PngColorType.Palette => bitDepth is 1 or 2 or 4 or 8,
            _ => bitDepth is 8 or 16,
        };
        if (!allowed || width <= 0 || height <= 0) throw new ArgumentException($"PNG: Farbart {type} mit {bitDepth} Bit");
        if (type == PngColorType.Palette && (palette is null || palette.Length < 3 || palette.Length > 768 || palette.Length % 3 != 0))
            throw new ArgumentException("PNG: Palette fehlt oder hat mehr als 256 Farben");

        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = (byte)bitDepth;
        header[9] = (byte)type;
        Chunk(output, "IHDR", header);
        if (dpi > 0)
        {
            // pixels per metre in both directions, unit 1 = metre (ISO/IEC 15948, 11.3.5.3)
            var physical = new byte[9];
            var perMetre = (uint)Math.Round(dpi / 0.0254);
            BinaryPrimitives.WriteUInt32BigEndian(physical, perMetre);
            BinaryPrimitives.WriteUInt32BigEndian(physical.AsSpan(4), perMetre);
            physical[8] = 1;
            Chunk(output, "pHYs", physical);
        }
        if (iccProfile is { Length: > 0 })
        {
            using var packed = new MemoryStream();
            packed.Write("ICC Profile\0\0"u8);   // name, terminator, compression method 0
            using (var z = new ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(iccProfile);
            Chunk(output, "iCCP", packed.ToArray());
        }
        // sBIT before PLTE and IDAT (4.2.6): written only when a channel has fewer bits than the sample depth
        var sampleDepth = type == PngColorType.Palette ? 8 : bitDepth;
        if (significantBits is not null && significantBits.Length == (type == PngColorType.Palette ? 3 : channels) &&
            significantBits.All(b => b >= 1 && b <= sampleDepth) && significantBits.Any(b => b < sampleDepth))
            Chunk(output, "sBIT", significantBits);
        if (type == PngColorType.Palette)
        {
            // no more entries than the bit depth can name (RFC 2083, 4.1.2): an indexed PDF image of 4 bits may still have hival 255
            // (docs/FEHLERGEDAECHTNIS.md, No. 941); the data cannot name the others
            palette = palette![..(Math.Min(palette.Length / 3, 1 << bitDepth) * 3)];
            Chunk(output, "PLTE", palette);
            if (paletteAlpha is { Length: > 0 })
            {
                // trailing opaque entries may be left out (11.3.2.1)
                var count = Math.Min(paletteAlpha.Length, palette.Length / 3);
                while (count > 0 && paletteAlpha[count - 1] == 255) count--;
                if (count > 0) Chunk(output, "tRNS", paletteAlpha.AsSpan(0, count));
            }
        }

        var rowBytes = checked((int)(((long)width * channels * bitDepth + 7) / 8));
        var bpp = Math.Max(1, channels * bitDepth / 8);   // byte distance for the filters
        var lastFilter = type == PngColorType.Palette || bitDepth < 8 ? 0 : 4;
        var current = new byte[rowBytes];
        var previous = new byte[rowBytes];
        var candidate = new byte[rowBytes];
        var best = new byte[rowBytes];
        // the compressed data goes out in IDAT chunks of 1 MB as it comes: memory for a few rows, not for the file
        using var data = new ChunkWriter(output, "IDAT", 1 << 20);
        using (var z = new ZLibStream(data, level, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                current.AsSpan().Clear();
                row(y, current);
                var bestType = 0;
                var bestSum = long.MaxValue;
                // RFC 2083, 9.6: palette images and depths below 8 bits with filter 0; otherwise per row the filter with the smallest sum of
                // its output bytes taken as signed differences — for filter 0 as well (FG 1015: before, its bytes counted unsigned)
                for (var f = 0; f <= lastFilter; f++)
                {
                    Filter(f, current, previous, candidate, bpp);
                    long sum = 0;
                    foreach (var b in candidate) sum += (sbyte)b < 0 ? -(sbyte)b : b;
                    if (sum >= bestSum) continue;
                    bestSum = sum;
                    bestType = f;
                    candidate.AsSpan().CopyTo(best);
                }
                z.WriteByte((byte)bestType);
                z.Write(best);
                (current, previous) = (previous, current);
            }
        }
        data.Flush();
        Chunk(output, "IEND", []);
    }

    /// <summary>
    /// Encodes a colour image of 8 bits per channel (RGB, or RGBA with <paramref name="withAlpha"/>): with at most 256 different colours as a
    /// palette image, as RFC 2083, 9.6 recommends (decided by the user on 22.11.2026), otherwise as RGB or RGBA. A first pass counts the colours
    /// and stops at the 257th; the second writes. The palette keeps the order in which the colours first appear, the entries with opacity
    /// below 255 first, so that tRNS stays short; the index depth is the smallest that names all entries (1, 2, 4 or 8 bits). The colours
    /// and opacities are exactly those of the rows.
    /// </summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="withAlpha">Whether the rows carry an alpha channel (RGBA) or not (RGB).</param>
    /// <param name="rows">Called once per pass: gives the writer of the rows (row <c>y</c> from the top, 3 or 4 bytes per pixel). The rows
    /// must be the same in both passes; a caller whose source can be read only once opens it again.</param>
    /// <param name="output">Where the file goes.</param>
    /// <param name="iccProfile">An RGB ICC profile to embed (iCCP), or null.</param>
    /// <param name="dpi">The resolution to note in the file (pHYs), or 0 for none.</param>
    /// <param name="level">How hard to compress.</param>
    /// <param name="significantBits">sBIT of the channels (3 or 4 values; a palette image keeps those of the colours), or null.</param>
    /// <returns>Whether a palette image was written.</returns>
    public static bool EncodeColour(int width, int height, bool withAlpha, Func<Action<int, Span<byte>>> rows, Stream output,
        byte[]? iccProfile = null, double dpi = 0, CompressionLevel level = CompressionLevel.SmallestSize, byte[]? significantBits = null)
    {
        var channels = withAlpha ? 4 : 3;
        var colours = new Dictionary<uint, int>();
        var order = new List<uint>();
        var line = new byte[checked(width * channels)];
        var count = rows();
        for (var y = 0; y < height && order.Count <= 256; y++)
        {
            line.AsSpan().Clear();
            count(y, line);
            for (var x = 0; x < width && order.Count <= 256; x++)
            {
                var c = Colour(line, x, channels);
                if (colours.TryAdd(c, 0)) order.Add(c);
            }
        }
        if (order.Count > 256)
        {
            Encode(width, height, 8, withAlpha ? PngColorType.Rgba : PngColorType.Rgb, rows(), output, iccProfile: iccProfile, dpi: dpi,
                level: level, significantBits: significantBits);
            return false;
        }
        // transparent entries first (tRNS names the first ones only), each group in the order of first appearance
        var entries = order.Where(c => (c & 0xFF) != 255).Concat(order.Where(c => (c & 0xFF) == 255)).ToArray();
        for (var i = 0; i < entries.Length; i++) colours[entries[i]] = i;
        var palette = new byte[entries.Length * 3];
        var alphas = new byte[entries.Length];
        for (var i = 0; i < entries.Length; i++)
            (palette[3 * i], palette[3 * i + 1], palette[3 * i + 2], alphas[i]) =
                ((byte)(entries[i] >> 24), (byte)(entries[i] >> 16), (byte)(entries[i] >> 8), (byte)entries[i]);
        var depth = entries.Length <= 2 ? 1 : entries.Length <= 4 ? 2 : entries.Length <= 16 ? 4 : 8;
        var write = rows();
        Encode(width, height, depth, PngColorType.Palette, (y, row) =>
        {
            line.AsSpan().Clear();
            write(y, line);
            for (var x = 0; x < width; x++)
            {
                var index = colours[Colour(line, x, channels)];
                var bit = x * depth;
                row[bit >> 3] |= (byte)(index << (8 - depth - (bit & 7)));
            }
        }, output, palette, alphas.Any(a => a != 255) ? alphas : null, iccProfile, dpi, level,
            significantBits is { Length: >= 3 } ? significantBits[..3] : null);
        return true;
    }

    /// <summary>A pixel as R G B A in one number (alpha 255 without an alpha channel).</summary>
    private static uint Colour(byte[] line, int x, int channels) =>
        (uint)line[x * channels] << 24 | (uint)line[x * channels + 1] << 16 | (uint)line[x * channels + 2] << 8 |
        (channels == 4 ? line[x * channels + 3] : 255u);

    private static void Filter(int type, byte[] row, byte[] prior, byte[] result, int bpp)
    {
        for (var i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0, b = prior[i], c = i >= bpp ? prior[i - bpp] : 0;
            result[i] = type switch
            {
                1 => (byte)(row[i] - a),
                2 => (byte)(row[i] - b),
                3 => (byte)(row[i] - (a + b) / 2),
                4 => (byte)(row[i] - Paeth(a, b, c)),
                _ => row[i],
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Collects written bytes and writes them as chunks of one type, each at most <c>size</c> bytes.</summary>
    private sealed class ChunkWriter(Stream output, string type, int size) : Stream
    {
        private readonly byte[] _buffer = new byte[size];
        private int _used;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> bytes)
        {
            while (bytes.Length > 0)
            {
                var take = Math.Min(bytes.Length, _buffer.Length - _used);
                bytes[..take].CopyTo(_buffer.AsSpan(_used));
                _used += take;
                bytes = bytes[take..];
                if (_used == _buffer.Length) Flush();
            }
        }

        /// <summary>Writes what is collected as one chunk.</summary>
        public override void Flush()
        {
            if (_used == 0) return;
            Chunk(output, type, _buffer.AsSpan(0, _used));
            _used = 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static void Chunk(Stream output, string type, ReadOnlySpan<byte> body)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head, body.Length);
        Encoding.ASCII.GetBytes(type, head[4..]);
        output.Write(head);
        output.Write(body);
        var crc = Crc32.Update(Crc32.Update(0xFFFFFFFF, head[4..]), body) ^ 0xFFFFFFFF;
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        output.Write(tail);
    }
}

/// <summary>CRC-32 as in PNG and zlib containers (ISO/IEC 15948, annex D).</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>Continues a running CRC (start with 0xFFFFFFFF, finish with ^ 0xFFFFFFFF).</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
