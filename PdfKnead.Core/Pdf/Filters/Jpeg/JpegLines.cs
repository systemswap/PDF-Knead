using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Pdf.Filters.Jpeg;

/// <summary>
/// How many image rows the beginning of a JPEG holds — for the progress of a page a network scanner sends while it still scans
/// (KONZEPT.md §5.25, S9e): the rows arrive in scan order, their number against the height in the header is how far the sheet is read.
/// Only the entropy-coded data is decoded (ITU-T T.81, F.2.2), no transform and no image memory, so a check costs a fraction of decoding the
/// page. Sequential Huffman JPEGs only (SOF0, SOF1) — what scanners send; for any other kind, or before the header is complete, the answer is
/// null and the caller shows no share.
/// </summary>
/// <remarks>
/// The data comes from the network: nothing is allocated by what the header claims (no planes, one block of 64 coefficients, Huffman tables of
/// at most 256 symbols); every segment length is checked against the data, the bit reader gives zeros past the end and says so, counting
/// stops there. A header claiming more pixels than the decoder accepts is refused, and one count takes at most <see cref="MaxTime"/> — crafted
/// data of very short codes could otherwise make it long; the caller then keeps the count before.
/// </remarks>
internal static class JpegLines
{
    /// <summary>The most time one count may take.</summary>
    public static readonly TimeSpan MaxTime = TimeSpan.FromMilliseconds(200);

    /// <summary>Counts the complete rows.</summary>
    /// <param name="data">The data received so far (from index 0).</param>
    /// <param name="length">How many bytes of <paramref name="data"/> are valid.</param>
    /// <returns>The complete rows and the height, or null.</returns>
    public static (int Rows, int Height)? Count(byte[] data, int length)
    {
        try
        {
            return Run(data, Math.Min(length, data.Length));
        }
        catch (Exception e) when (e is JpegException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    private static (int Rows, int Height)? Run(byte[] data, int end)
    {
        if (end < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        var dc = new JpegHuffmanTable?[4];
        var ac = new JpegHuffmanTable?[4];
        int width = 0, height = 0, interval = 0;
        (int Id, int H, int V)[]? components = null;
        for (var at = 2; at + 4 <= end;)
        {
            if (data[at] != 0xFF) return null;
            var marker = data[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }
            var length = data[at + 2] << 8 | data[at + 3];
            if (length < 2 || at + 2 + length > end) return null;   // the segment has not fully arrived yet
            var s = data.AsSpan(at + 4, length - 2);
            switch (marker)
            {
                case 0xC0 or 0xC1:
                    if (s.Length < 6) return null;
                    height = s[1] << 8 | s[2];
                    width = s[3] << 8 | s[4];
                    var n = s[5];
                    if (n is < 1 or > 4 || s.Length < 6 + 3 * n || width == 0 || height == 0) return null;
                    if ((long)width * height > PdfImageDecoder.MaxPixels) return null;
                    components = new (int, int, int)[n];
                    for (var i = 0; i < n; i++)
                    {
                        int h = s[7 + 3 * i] >> 4, v = s[7 + 3 * i] & 15;
                        if (h is < 1 or > 4 || v is < 1 or > 4) return null;
                        components[i] = (s[6 + 3 * i], h, v);
                    }
                    break;
                case >= 0xC2 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC):
                    return null;   // progressive, arithmetic, lossless, hierarchical: no rows in scan order to count
                case 0xC4:
                    if (!Huffman(s, dc, ac)) return null;
                    break;
                case 0xDD:
                    if (s.Length < 2) return null;
                    interval = s[0] << 8 | s[1];
                    break;
                case 0xDA:
                    return components is null ? null : Scan(data, at + 2 + length, end, s, components, width, height, interval, dc, ac);
            }
            at += 2 + length;
        }
        return null;
    }

    private static bool Huffman(ReadOnlySpan<byte> s, JpegHuffmanTable?[] dc, JpegHuffmanTable?[] ac)
    {
        Span<int> counts = stackalloc int[17];   // per code length 1–16, index 0 unused (as the table expects)
        for (var at = 0; at < s.Length;)
        {
            if (at + 17 > s.Length) return false;
            int tc = s[at] >> 4, th = s[at] & 15;
            if (tc > 1 || th > 3) return false;
            var total = 0;
            for (var l = 1; l <= 16; l++) total += counts[l] = s[at + l];
            if (total > 256 || at + 17 + total > s.Length) return false;
            var table = new JpegHuffmanTable(counts, s.Slice(at + 17, total).ToArray());
            (tc == 0 ? dc : ac)[th] = table;
            at += 17 + total;
        }
        return true;
    }

    private static (int Rows, int Height)? Scan(byte[] data, int start, int end, ReadOnlySpan<byte> header, (int Id, int H, int V)[] components,
        int width, int height, int interval, JpegHuffmanTable?[] dc, JpegHuffmanTable?[] ac)
    {
        if (header.Length < 1) return null;
        int ns = header[0];
        if (ns is < 1 or > 4 || header.Length < 1 + 2 * ns) return null;
        var scanDc = new JpegHuffmanTable?[ns];
        var scanAc = new JpegHuffmanTable?[ns];
        var parts = new (int H, int V)[ns];
        for (var i = 0; i < ns; i++)
        {
            var id = header[1 + 2 * i];
            var c = Array.FindIndex(components, x => x.Id == id);
            if (c < 0) return null;
            scanDc[i] = dc[header[2 + 2 * i] >> 4 & 3];
            scanAc[i] = ac[header[2 + 2 * i] & 3];
            parts[i] = (components[c].H, components[c].V);
        }
        int hMax = components.Max(c => c.H), vMax = components.Max(c => c.V);
        // one component alone is coded block by block (T.81, A.2.2), several in MCUs of their sampling factors
        var single = ns == 1;
        int mcuWidth = single ? 8 * hMax / parts[0].H : 8 * hMax, mcuHeight = single ? 8 * vMax / parts[0].V : 8 * vMax;
        int mcusWide = (width + mcuWidth - 1) / mcuWidth, mcusHigh = (height + mcuHeight - 1) / mcuHeight;
        var bits = new JpegBits(data, start, end);
        var entropy = new JpegHuffman(bits, scanDc, scanAc);
        entropy.Reset();
        Span<short> block = stackalloc short[64];
        long mcus = (long)mcusWide * mcusHigh, done = 0;
        var restarts = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (long m = 0; m < mcus; m++)
        {
            if ((m & 4095) == 0 && m > 0 && clock.Elapsed > MaxTime) return null;
            if (interval > 0 && m > 0 && m % interval == 0 && !entropy.Restart(restarts++ & 7)) break;
            for (var i = 0; i < ns; i++)
                for (var k = single ? 1 : parts[i].H * parts[i].V; k > 0; k--)
                {
                    entropy.Sequential(i, block);
                    block.Clear();
                }
            // an MCU counts only if its bits all came from data: past the data the reader supplies zeros
            if (bits.RanShort || entropy.Dead) break;
            done = m + 1;
        }
        var rows = (int)Math.Min(height, done / mcusWide * mcuHeight);
        return (rows, height);
    }
}
