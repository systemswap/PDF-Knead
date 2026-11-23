using System.Buffers.Binary;
using PdfKnead.Core.Imaging;

namespace PdfKnead.Core.Scanning.Wia;

/// <summary>The bytes of one page as the driver wrote them, read piece by piece (a page can have hundreds of megabytes).</summary>
internal interface IPageData
{
    /// <summary>The number of bytes.</summary>
    long Length { get; }

    /// <summary>Copies bytes from <paramref name="offset"/>; past the end the rest of <paramref name="target"/> stays as it is.</summary>
    /// <returns>The number of bytes copied.</returns>
    int Read(long offset, Span<byte> target);
}

/// <summary>Page data in one array (tests, small pages).</summary>
internal sealed class ArrayPageData(byte[] data) : IPageData
{
    public long Length => data.LongLength;

    public int Read(long offset, Span<byte> target)
    {
        if (offset >= data.LongLength) return 0;
        var count = (int)Math.Min(target.Length, data.LongLength - offset);
        data.AsSpan((int)offset, count).CopyTo(target);
        return count;
    }
}

/// <summary>
/// Turns what a WIA driver delivers for a page into a <see cref="ScannedImage"/>: BMP (own reader with limits — header checked before
/// anything is allocated, rows from the bottom or the top, rows padded to four bytes, 1/4/8 bits with palette, 24 and 32 bits, with or
/// without the file header) and, for drivers that refuse BMP, PNG. The size always comes from the image itself, never from the item's
/// properties (a WSD class driver reported 2479 pixels per line for an extent of 2480).
/// </summary>
internal static class WiaImage
{
    /// <summary>The most bytes of samples for one page (as for SANE: A4 at 1200 dpi in colour has about 420 MB).</summary>
    internal const long MaxPixelBytes = 1_600_000_000;

    /// <summary>The widest and tallest page in pixels.</summary>
    internal const int MaxSide = 1_000_000;

    /// <summary>Decodes a page.</summary>
    /// <param name="data">The page as the driver wrote it.</param>
    /// <param name="dpi">The resolution the item was set to (BMP headers of drivers often carry none or a wrong one).</param>
    /// <param name="gray">Whether grey was asked for: a grey page delivered with three equal channels then gets one.</param>
    /// <returns>The page.</returns>
    /// <exception cref="ScanException">The data is no image the program reads, or it is broken.</exception>
    public static ScannedImage Decode(IPageData data, int dpi, bool gray)
    {
        Span<byte> head = stackalloc byte[16];
        head.Clear();
        data.Read(0, head);
        if (head[0] == 'B' && head[1] == 'M') return Bmp(data, 14, dpi, gray);
        // a bitmap without the file header (memory BMP): the info header first
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) is 40 or 52 or 56 or 108 or 124) return Bmp(data, 0, dpi, gray);
        if (PngDecoder.IsPng(head) && data.Length < int.MaxValue)
        {
            var bytes = new byte[data.Length];
            data.Read(0, bytes);
            RgbaImage png;
            try
            {
                png = PngDecoder.Decode(bytes, complete: true);   // a scanner page is whole (No. 965)
            }
            catch (Exception e) when (e is InvalidDataException or OverflowException)
            {
                throw Unknown();
            }
            if (png.Damaged) throw Unknown();   // read past an error: a page with wrong pixels is not taken (FG 1013)
            return FromRgba(png, dpi, gray);
        }
        throw Unknown();
    }

    private static ScanException Unknown() => new(Tr.T("Der Scanner liefert ein unbekanntes Bildformat."));

    private static ScanException Broken() => new(Tr.T("Der Scanner hat ein unvollständiges oder beschädigtes Bild geliefert."));

    private static ScannedImage Bmp(IPageData data, long infoAt, int dpi, bool gray)
    {
        Span<byte> info = stackalloc byte[124 + 12];
        info.Clear();
        if (data.Read(infoAt, info) < 40) throw Broken();
        var headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(info);
        if (headerSize is not (40 or 52 or 56 or 108 or 124)) throw Unknown();   // the old 12-byte core header is not written by scanner drivers
        var width = BinaryPrimitives.ReadInt32LittleEndian(info[4..]);
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(info[8..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(info[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(info[16..]);
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(info[32..]);
        var topDown = rawHeight < 0;
        // a height of 0: older drivers leave it so when the length of the paper was not known — then the rows that came count (bottom-up)
        var height = rawHeight == int.MinValue ? -1 : rawHeight == 0 ? MaxSide : Math.Abs(rawHeight);
        if (width is <= 0 or > MaxSide || height is <= 0 or > MaxSide) throw Broken();
        if (bits is not (1 or 4 or 8 or 24 or 32)) throw Unknown();
        // BI_RGB, or BI_BITFIELDS for 32 bits (the masks are read); run lengths, JPEG and PNG inside a BMP are not written by scanners
        const uint BiRgb = 0, BiBitfields = 3;
        if (compression != BiRgb && !(compression == BiBitfields && bits == 32)) throw Unknown();
        uint red = 0x00FF0000, green = 0x0000FF00, blue = 0x000000FF;
        var masksAfter = 0;
        if (compression == BiBitfields)
        {
            // in the 40-byte header the masks follow it; in the larger ones they are part of it
            var masks = info[40..];
            (red, green, blue) = (BinaryPrimitives.ReadUInt32LittleEndian(masks), BinaryPrimitives.ReadUInt32LittleEndian(masks[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(masks[8..]));
            if (headerSize == 40) masksAfter = 12;
            if (!ByteMask(red) || !ByteMask(green) || !ByteMask(blue)) throw Unknown();
        }

        // the palette: for 1, 4 and 8 bits, as many entries as used (0 = all), at most 2^bits
        var paletteSize = bits <= 8 ? (int)(colorsUsed is > 0 and var used && used <= 1u << bits ? used : 1u << bits) : 0;
        var palette = new byte[paletteSize * 4];
        var paletteAt = infoAt + headerSize + masksAfter;
        if (paletteSize > 0 && data.Read(paletteAt, palette) < palette.Length) throw Broken();
        var pixelsAt = infoAt == 14
            ? BinaryPrimitives.ReadUInt32LittleEndian(Header(data))   // bfOffBits of the file header
            : paletteAt + palette.Length;
        if (pixelsAt < paletteAt + palette.Length || pixelsAt > data.Length) throw Broken();

        var stride = ((long)width * bits + 31) / 32 * 4;
        // the rows that arrived: a driver that announces more than it delivers (feeders with paper of unknown length) gives the shorter page
        var rows = (int)Math.Min(height, (data.Length - pixelsAt) / stride);
        if (rows <= 0) throw Broken();

        var grayPalette = paletteSize > 0 && Enumerable.Range(0, paletteSize).All(i => palette[i * 4] == palette[i * 4 + 1] && palette[i * 4 + 1] == palette[i * 4 + 2]);
        var channels = bits <= 8 ? (grayPalette ? 1 : 3) : 3;
        if ((long)width * rows * channels > Math.Min(MaxPixelBytes, Array.MaxLength))
            throw new ScanException(Tr.T("Die Seite wäre zu groß — bitte eine kleinere Auflösung oder Fläche wählen."));
        var pixels = new byte[(long)width * rows * channels];
        var row = new byte[stride];
        // bottom-up rows: the first stored row is the last of the image; of a short bottom-up page the rows that came are its lower part
        for (var stored = 0; stored < rows; stored++)
        {
            data.Read(pixelsAt + stored * stride, row);
            var y = topDown ? stored : rows - 1 - stored;
            var target = pixels.AsSpan((int)((long)y * width * channels), width * channels);
            switch (bits)
            {
                case 24:
                    for (var x = 0; x < width; x++) (target[x * 3], target[x * 3 + 1], target[x * 3 + 2]) = (row[x * 3 + 2], row[x * 3 + 1], row[x * 3]);
                    break;
                case 32:
                    for (var x = 0; x < width; x++)
                    {
                        var value = BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(x * 4));
                        (target[x * 3], target[x * 3 + 1], target[x * 3 + 2]) = (Channel(value, red), Channel(value, green), Channel(value, blue));
                    }
                    break;
                default:
                    for (var x = 0; x < width; x++)
                    {
                        var bit = x * bits;
                        var index = (row[bit >> 3] >> (8 - bits - (bit & 7))) & ((1 << bits) - 1);
                        if (index >= paletteSize) index = paletteSize - 1;   // an index past the palette: the last entry, never a read past it
                        var entry = index * 4;
                        if (channels == 1) target[x] = palette[entry];
                        else (target[x * 3], target[x * 3 + 1], target[x * 3 + 2]) = (palette[entry + 2], palette[entry + 1], palette[entry]);
                    }
                    break;
            }
        }
        var blackWhite = bits == 1 && channels == 1 && paletteSize == 2 && (palette[0] is 0 or 255) && (palette[4] is 0 or 255) && palette[0] != palette[4];
        var image = new ScannedImage(width, rows, channels, dpi, pixels, blackWhite);
        return gray && channels == 3 ? ToGray(image) : image;
    }

    private static ReadOnlySpan<byte> Header(IPageData data)
    {
        var header = new byte[14];
        data.Read(0, header);
        return header.AsSpan(10);
    }

    /// <summary>Whether a 32-bit mask selects one whole byte (the masks scanner drivers write; others are refused rather than guessed).</summary>
    private static bool ByteMask(uint mask) => mask is 0x000000FF or 0x0000FF00 or 0x00FF0000 or 0xFF000000;

    private static byte Channel(uint value, uint mask) => (byte)((value & mask) >> System.Numerics.BitOperations.TrailingZeroCount(mask));

    private static ScannedImage FromRgba(RgbaImage png, int dpi, bool gray)
    {
        var count = (long)png.Width * png.Height;
        var allGray = true;
        for (long i = 0; i < count && allGray; i++)
            allGray = png.Pixels[i * 4] == png.Pixels[i * 4 + 1] && png.Pixels[i * 4 + 1] == png.Pixels[i * 4 + 2];
        var channels = allGray || gray ? 1 : 3;
        var pixels = new byte[count * channels];
        for (long i = 0; i < count; i++)
        {
            var (r, g, b) = (png.Pixels[i * 4], png.Pixels[i * 4 + 1], png.Pixels[i * 4 + 2]);
            if (channels == 1) pixels[i] = allGray ? r : Luma(r, g, b);
            else (pixels[i * 3], pixels[i * 3 + 1], pixels[i * 3 + 2]) = (r, g, b);
        }
        return new ScannedImage(png.Width, png.Height, channels, dpi, pixels);
    }

    /// <summary>One channel from three: the luma of ITU-R BT.601 (as for greyscale in the rest of the program).</summary>
    private static ScannedImage ToGray(ScannedImage image)
    {
        var count = (long)image.Width * image.Height;
        var pixels = new byte[count];
        for (long i = 0; i < count; i++) pixels[i] = Luma(image.Pixels[i * 3], image.Pixels[i * 3 + 1], image.Pixels[i * 3 + 2]);
        return image with { Channels = 1, Pixels = pixels, BlackWhite = false };
    }

    private static byte Luma(byte r, byte g, byte b) => (byte)((r * 299 + g * 587 + b * 114 + 500) / 1000);
}
