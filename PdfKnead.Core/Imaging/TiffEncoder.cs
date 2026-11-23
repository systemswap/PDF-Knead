using System.Buffers.Binary;
using System.IO.Compression;

namespace PdfKnead.Core.Imaging;

/// <summary>What a page of a TIFF file holds.</summary>
public enum TiffPageKind
{
    /// <summary>One bit per pixel, packed as CCITT Group 4 (TIFF 6.0, section 11, compression 4).</summary>
    BlackWhite,

    /// <summary>Eight-bit greyscale, Deflate with horizontal differencing.</summary>
    Gray,

    /// <summary>Eight bits per channel RGB, Deflate with horizontal differencing.</summary>
    Rgb,
}

/// <summary>One page of a TIFF file.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Kind">What the page holds.</param>
/// <param name="Dpi">The resolution noted in the file (XResolution, YResolution in pixels per inch).</param>
/// <param name="Row">Writes row <c>y</c> (from the top): black and white packed most significant bit first with 1 = black, greyscale one
/// byte, RGB three bytes per pixel.</param>
public sealed record TiffPage(int Width, int Height, TiffPageKind Kind, double Dpi, Action<int, Span<byte>> Row);

/// <summary>
/// Writes TIFF (TIFF 6.0). CMYK images losslessly (section 16 „CMYK Images“): 8 or 16 bits per channel, optionally with an
/// unassociated alpha channel (ExtraSamples = 2), Deflate (compression 8, zlib as in the Adobe extension) with horizontal differencing
/// (predictor 2), and the ICC profile in tag 34675 — PNG cannot hold CMYK. Pages of scans and documents, one or many per file: greyscale and
/// RGB with Deflate and predictor 2, black and white with CCITT Group 4, each with its resolution.
/// </summary>
public static class TiffEncoder
{
    // about 8 KB of raw data per strip, as TIFF 6.0 recommends (sections 7 and 8), and a single row per strip when a row is larger (section 7,
    // a should); no strip packs to more than 64 KB, as section 8 asks for old readers (decisions of the user 19.11.2026, M2 row 211, and
    // 22.11.2026, FG 1019; before 32 KB, before that 256 KB; docs/FEHLERGEDAECHTNIS.md, No. 964)
    private const int StripBytes = 8 << 10;

    // black and white: Group 4 can grow a row of single-pixel runs to about six bits per pixel (horizontal mode with the shortest codes), so at
    // most 8 KB of raw bits per strip keep it below 64 KB packed — the size TIFF 6.0 itself recommends (No. 964)
    private const int BilevelStripBytes = 8 << 10;

    /// <summary>Encodes a CMYK image.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="bitDepth">8 or 16 bits per channel.</param>
    /// <param name="alpha">Whether a fifth channel holds the opacity.</param>
    /// <param name="row">Writes row <c>y</c> (from the top): C, M, Y, K (and A) per pixel, 16-bit values big-endian.</param>
    /// <param name="iccProfile">The CMYK profile, or null.</param>
    /// <param name="output">Where the file goes.</param>
    /// <exception cref="ArgumentException">Unsupported bit depth or size.</exception>
    /// <param name="dpi">The resolution on both axes — that of the image as drawn on its page — or null when it is not known: then the longest
    /// side measures four inches, as TIFF 6.0, section 8 advises for an arbitrary resolution (FG 1019; before 72 per inch always).</param>
    public static void EncodeCmyk(int width, int height, int bitDepth, bool alpha, Action<int, Span<byte>> row, byte[]? iccProfile, Stream output,
        (double X, double Y)? dpi = null)
    {
        if (bitDepth is not (8 or 16) || width <= 0 || height <= 0) throw new ArgumentException($"TIFF: {bitDepth} Bit, {width} × {height}");
        var channels = alpha ? 5 : 4;
        var fourInches = Math.Max(width, height) / 4.0;
        (double X, double Y) resolution = dpi is { X: > 0, Y: > 0 } d ? d : (fourInches, fourInches);
        var bytesPerSample = bitDepth / 8;
        var rowBytes = checked(width * channels * bytesPerSample);
        var rowsPerStrip = Math.Clamp(StripBytes / rowBytes, 1, height);

        // the strips first (their sizes are only known after compressing), the directory behind them
        var strips = new List<byte[]>();
        var line = new byte[rowBytes];
        for (var top = 0; top < height; top += rowsPerStrip)
        {
            using var packed = new MemoryStream();
            using (var z = new ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                for (var y = top; y < Math.Min(height, top + rowsPerStrip); y++)
                {
                    line.AsSpan().Clear();
                    row(y, line);
                    Difference(line, channels, bytesPerSample);
                    z.Write(line);
                }
            }
            strips.Add(packed.ToArray());
        }

        var header = new byte[8];
        "MM"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 42);
        long offset = 8;
        var stripOffsets = new long[strips.Count];
        for (var i = 0; i < strips.Count; i++)
        {
            stripOffsets[i] = offset;
            offset += strips[i].Length + (strips[i].Length & 1);   // word boundary (TIFF 6.0, section 2)
        }

        var entries = new List<(ushort Tag, ushort Type, uint Count, byte[] Value)>
        {
            (256, 4, 1, U32(width)),
            (257, 4, 1, U32(height)),
            (258, 3, (uint)channels, Repeat(U16(bitDepth), channels)),
            (259, 3, 1, U16(8)),             // Deflate
            (262, 3, 1, U16(5)),             // separated (CMYK)
            (273, 4, (uint)strips.Count, stripOffsets.SelectMany(o => U32((int)o)).ToArray()),
            (277, 3, 1, U16(channels)),
            (278, 4, 1, U32(rowsPerStrip)),
            (279, 4, (uint)strips.Count, strips.SelectMany(s => U32(s.Length)).ToArray()),
            // XResolution and YResolution are required of every Baseline image, and a separated file meets those requirements too
            // (TIFF 6.0, sections 8 and 16; No. 942); an image of a PDF has no resolution of its own — that of its drawn size, else four inches
            (282, 5, 1, Rational(resolution.X)),
            (283, 5, 1, Rational(resolution.Y)),
            (284, 3, 1, U16(1)),             // chunky
            (296, 3, 1, U16(2)),             // inch
            (317, 3, 1, U16(2)),             // horizontal differencing
            (332, 3, 1, U16(1)),             // InkSet CMYK
        };
        if (alpha) entries.Add((338, 3, 1, U16(2)));   // ExtraSamples: unassociated alpha
        if (iccProfile is { Length: > 0 }) entries.Add((34675, 7, (uint)iccProfile.Length, iccProfile));

        // values longer than four bytes go between the strips and the directory
        var extra = new List<byte[]>();
        var valueOffsets = new long[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Value.Length <= 4) continue;
            valueOffsets[i] = offset;
            extra.Add(entries[i].Value);
            offset += entries[i].Value.Length + (entries[i].Value.Length & 1);
        }
        if (offset > uint.MaxValue - 1024) throw new ArgumentException("TIFF: größer als 4 GB");
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)offset);

        output.Write(header);
        foreach (var strip in strips)
        {
            output.Write(strip);
            if ((strip.Length & 1) != 0) output.WriteByte(0);
        }
        foreach (var value in extra)
        {
            output.Write(value);
            if ((value.Length & 1) != 0) output.WriteByte(0);
        }
        var directory = new byte[2 + entries.Count * 12 + 4];
        BinaryPrimitives.WriteUInt16BigEndian(directory, (ushort)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var at = directory.AsSpan(2 + i * 12, 12);
            var (tag, type, count, value) = entries[i];
            BinaryPrimitives.WriteUInt16BigEndian(at, tag);
            BinaryPrimitives.WriteUInt16BigEndian(at[2..], type);
            BinaryPrimitives.WriteUInt32BigEndian(at[4..], count);
            if (value.Length <= 4) value.CopyTo(at[8..]);   // left-aligned in the field
            else BinaryPrimitives.WriteUInt32BigEndian(at[8..], (uint)valueOffsets[i]);
        }
        output.Write(directory);   // the last four bytes stay 0: no further directory
    }

    /// <summary>Encodes pages into one file (several image directories one after the other, TIFF 6.0, section 2).</summary>
    /// <param name="count">The number of pages; at least one.</param>
    /// <param name="page">Gives page <c>i</c>; asked for in order, one at a time — only one page is held in memory.</param>
    /// <param name="output">Where the file goes; it is written front to back (need not be seekable).</param>
    /// <param name="cancel">Stops between strips.</param>
    /// <exception cref="ArgumentException">No page, a page without pixels, or the file would exceed 4 GB.</exception>
    /// <remarks>
    /// Each page is laid out as directory, long values, image data; its size is known once the page is packed, so the offset of the next
    /// directory can be written into the directory before it. Black and white pages are WhiteIsZero with T6Options 0, each strip its own
    /// Group 4 encoding of at most 8 KB of raw bits (a multiple of eight rows where it fits); the other pages strips of about 8 KB of raw data —
    /// no strip above 64 KB packed (TIFF 6.0, section 8). Greyscale and RGB pages carry their profile and the TransferFunction of the sRGB curve.
    /// </remarks>
    /// <param name="firstPage">The number of the first page in its document (0 for the first): a file of one page out of several says which
    /// one in PageNumber.</param>
    /// <param name="documentPages">The pages of the document, or 0 when they are all in this file.</param>
    public static void EncodePages(int count, Func<int, TiffPage> page, Stream output, CancellationToken cancel = default, int firstPage = 0,
        int documentPages = 0)
    {
        if (count <= 0) throw new ArgumentException("TIFF: keine Seite", nameof(count));
        // PageNumber counts the pages of the document (TIFF 6.0, section 12), also when each page is a file of its own (FG 1019: before (0, 1)
        // in each of them); NewSubfileType bit 1 marks a page of a multi-page document (No. 1017)
        var total = Math.Max(documentPages, firstPage + count);
        var header = new byte[8];
        "MM"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 8);
        output.Write(header);
        long offset = 8;
        for (var index = 0; index < count; index++)
        {
            var p = page(index);
            if (p.Width <= 0 || p.Height <= 0) throw new ArgumentException($"TIFF: Seite {index + 1} mit {p.Width} × {p.Height}");
            var (strips, rowsPerStrip) = Pack(p, cancel);
            var bw = p.Kind == TiffPageKind.BlackWhite;
            var channels = p.Kind == TiffPageKind.Rgb ? 3 : 1;
            var resolution = Rational(p.Dpi);
            var entries = new List<(ushort Tag, ushort Type, uint Count, byte[] Value)>
            {
                // NewSubfileType: bit 1 marks a page of a multi-page image (TIFF 6.0, section 8; FG 1017: before 0 on every page)
                (254, 4, 1, U32(total > 1 ? 2 : 0)),
                (256, 4, 1, U32(p.Width)),
                (257, 4, 1, U32(p.Height)),
                (258, 3, (uint)channels, Repeat(U16(bw ? 1 : 8), channels)),
                (259, 3, 1, U16(bw ? 4 : 8)),                          // CCITT Group 4 or Deflate
                (262, 3, 1, U16(bw ? 0 : channels == 3 ? 2 : 1)),      // WhiteIsZero, RGB, BlackIsZero
                (273, 4, (uint)strips.Count, new byte[4 * strips.Count]),   // filled in below
                (277, 3, 1, U16(channels)),
                (278, 4, 1, U32(rowsPerStrip)),
                (279, 4, (uint)strips.Count, strips.SelectMany(s => U32(s.Length)).ToArray()),
                (282, 5, 1, resolution),
                (283, 5, 1, resolution),
                (284, 3, 1, U16(1)),                                   // chunky
            };
            // FillOrder stated with CCITT data, as TIFF 6.0, section 11 recommends: 1, the most significant bit first (No. 942)
            if (bw) entries.Insert(entries.FindIndex(e => e.Tag == 273), (266, 3, 1, U16(1)));
            if (bw) entries.Add((293, 4, 1, U32(0)));                // T6Options: no uncompressed mode
            entries.Add((296, 3, 1, U16(2)));                          // inch
            entries.Add((297, 3, 2, [.. U16(firstPage + index), .. U16(total)]));   // PageNumber
            // the values are not linear (rendered or scanned in the sRGB curve): TIFF 6.0, section 17 asks for a TransferFunction then — the
            // profile below says the same to readers that know it (decided by the user on 22.11.2026, FG 1019)
            if (!bw) entries.Add((301, 3, 256, SrgbTransferFunction.Value));
            if (!bw) entries.Add((317, 3, 1, U16(2)));               // horizontal differencing
            // the colour of the values: without a profile TIFF takes grey as linear (section 17; the values are rendered or scanned, so in the
            // sRGB curve) and RGB with a gamma of about 2.2 (section 20) — the profile says exactly what they are (decided on
            // 20.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 980). Black and white needs none.
            if (!bw)
            {
                var profile = channels == 3 ? Pdf.Graphics.Icc.CalibratedProfile.Srgb() : Pdf.Graphics.Icc.CalibratedProfile.SrgbGray();
                entries.Add((34675, 7, (uint)profile.Length, profile));
            }

            // layout: directory, long values, strips; the next directory right behind the strips
            var directoryAt = offset;
            var at = directoryAt + 2 + entries.Count * 12 + 4;
            var valueOffsets = new long[entries.Count];
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].Value.Length <= 4) continue;
                valueOffsets[i] = at;
                at += entries[i].Value.Length + (entries[i].Value.Length & 1);
            }
            var stripOffsets = entries.FindIndex(e => e.Tag == 273);
            for (var i = 0; i < strips.Count; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(entries[stripOffsets].Value.AsSpan(4 * i), (uint)at);
                at += strips[i].Length + (strips[i].Length & 1);
            }
            if (at > uint.MaxValue - 1024) throw new ArgumentException("TIFF: größer als 4 GB");

            var directory = new byte[2 + entries.Count * 12 + 4];
            BinaryPrimitives.WriteUInt16BigEndian(directory, (ushort)entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                var field = directory.AsSpan(2 + i * 12, 12);
                var (tag, type, n, value) = entries[i];
                BinaryPrimitives.WriteUInt16BigEndian(field, tag);
                BinaryPrimitives.WriteUInt16BigEndian(field[2..], type);
                BinaryPrimitives.WriteUInt32BigEndian(field[4..], n);
                if (value.Length <= 4) value.CopyTo(field[8..]);   // left-aligned in the field
                else BinaryPrimitives.WriteUInt32BigEndian(field[8..], (uint)valueOffsets[i]);
            }
            if (index < count - 1) BinaryPrimitives.WriteUInt32BigEndian(directory.AsSpan(directory.Length - 4), (uint)at);
            output.Write(directory);
            foreach (var value in entries.Where(e => e.Value.Length > 4).Select(e => e.Value))
            {
                output.Write(value);
                if ((value.Length & 1) != 0) output.WriteByte(0);
            }
            foreach (var strip in strips)
            {
                output.Write(strip);
                if ((strip.Length & 1) != 0) output.WriteByte(0);
            }
            offset = at;
        }
    }

    /// <summary>The image data of a page in strips, and the rows per strip.</summary>
    private static (List<byte[]> Strips, int RowsPerStrip) Pack(TiffPage p, CancellationToken cancel)
    {
        if (p.Kind == TiffPageKind.BlackWhite)
        {
            // strips as for the other pages, each its own Group 4 encoding (TIFF 6.0, section 11: every strip starts afresh; No. 964 — before
            // the whole page was one strip)
            var rowBytes = (p.Width + 7) / 8;
            // a multiple of eight rows per strip where it fits, as section 11 advises for the interchange of T.6 data (FG 1018)
            var fit = BilevelStripBytes / rowBytes;
            var rows = Math.Clamp(fit >= 8 ? fit / 8 * 8 : fit, 1, p.Height);
            var parts = new List<byte[]>();
            for (var top = 0; top < p.Height; top += rows)
            {
                cancel.ThrowIfCancellationRequested();
                var count = Math.Min(rows, p.Height - top);
                var bits = new byte[(long)rowBytes * count];
                for (var y = 0; y < count; y++) p.Row(top + y, bits.AsSpan(y * rowBytes, rowBytes));
                parts.Add(Pdf.Filters.CcittEncoder.EncodeG4(bits, p.Width, count, rowBytes, blackIs1: true));
            }
            return (parts, rows);
        }
        var channels = p.Kind == TiffPageKind.Rgb ? 3 : 1;
        var lineBytes = checked(p.Width * channels);
        var rowsPerStrip = Math.Clamp(StripBytes / lineBytes, 1, p.Height);
        var strips = new List<byte[]>();
        var line = new byte[lineBytes];
        for (var top = 0; top < p.Height; top += rowsPerStrip)
        {
            cancel.ThrowIfCancellationRequested();
            using var packed = new MemoryStream();
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (var y = top; y < Math.Min(p.Height, top + rowsPerStrip); y++)
                {
                    line.AsSpan().Clear();
                    p.Row(y, line);
                    Difference(line, channels, 1);
                    z.Write(line);
                }
            }
            strips.Add(packed.ToArray());
        }
        return (strips, rowsPerStrip);
    }

    /// <summary>
    /// The TransferFunction of 8-bit values in the sRGB curve (TIFF 6.0, section 20): for each value its linear intensity, 0 … 65535, one table
    /// for all channels; the curve of IEC 61966-2-1.
    /// </summary>
    private static readonly Lazy<byte[]> SrgbTransferFunction = new(() =>
    {
        var table = new byte[512];
        for (var v = 0; v < 256; v++)
        {
            var c = v / 255.0;
            var linear = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2 * v), (ushort)Math.Round(linear * 65535));
        }
        return table;
    });

    /// <summary>A resolution as a TIFF RATIONAL (hundredths of a pixel per inch).</summary>
    private static byte[] Rational(double dpi)
    {
        var value = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(value, (uint)Math.Round(Math.Clamp(dpi, 1, 100_000) * 100));
        BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(4), 100);
        return value;
    }

    /// <summary>Predictor 2: each sample minus the same channel of the pixel to its left (per byte or per 16-bit value), right to left.</summary>
    private static void Difference(Span<byte> line, int channels, int bytesPerSample)
    {
        var stride = channels * bytesPerSample;
        if (bytesPerSample == 1)
        {
            for (var i = line.Length - 1; i >= stride; i--) line[i] -= line[i - stride];
            return;
        }
        for (var i = line.Length - 2; i >= stride; i -= 2)
        {
            var v = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(line[i..]) - BinaryPrimitives.ReadUInt16BigEndian(line[(i - stride)..]));
            BinaryPrimitives.WriteUInt16BigEndian(line[i..], v);
        }
    }

    private static byte[] U16(int v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
        return b;
    }

    private static byte[] U32(int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v);
        return b;
    }

    private static byte[] Repeat(byte[] value, int count) => Enumerable.Repeat(value, count).SelectMany(v => v).ToArray();
}
