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
    /// <exception cref="ArgumentException">Bit depth and colour type do not fit together, or the palette is missing.</exception>
    public static void Encode(int width, int height, int bitDepth, PngColorType type, Action<int, Span<byte>> row, Stream output,
        byte[]? palette = null, byte[]? paletteAlpha = null, byte[]? iccProfile = null, double dpi = 0,
        CompressionLevel level = CompressionLevel.SmallestSize)
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
                for (var f = 0; f <= 4; f++)
                {
                    Filter(f, current, previous, candidate, bpp);
                    long sum = 0;
                    foreach (var b in candidate) sum += f == 0 ? b : (sbyte)b < 0 ? -(sbyte)b : b;
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
