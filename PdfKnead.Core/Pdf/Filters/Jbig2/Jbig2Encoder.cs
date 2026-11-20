namespace PdfKnead.Core.Pdf.Filters.Jbig2;

/// <summary>
/// MQ arithmetic encoder (ITU-T T.88, E.2; the same as JPEG 2000, ITU-T T.800, C.2), the counterpart of <see cref="MqDecoder"/>, with the same
/// state tables and contexts ((state index &lt;&lt; 1) | MPS).
/// </summary>
internal sealed class MqEncoder
{
    private byte[] _buffer = new byte[4096];
    private int _bp;   // position of byte B; _buffer[0] is a placeholder before the start (ITU-T T.800, C.2.8: BP = BPST − 1)
    private uint _a = 0x8000, _c;
    private int _ct = 12;

    /// <summary>Encodes one bit in the context <paramref name="cx"/>[<paramref name="index"/>] (ITU-T T.88, E.2.2).</summary>
    public void Encode(byte[] cx, int index, int bit)
    {
        int state = cx[index], i = state >> 1, mps = state & 1;
        uint qe = MqDecoder.Qe[i];
        _a -= qe;
        if (bit == mps)
        {
            if ((_a & 0x8000) != 0)
            {
                _c += qe;
                return;
            }
            if (_a < qe) _a = qe;
            else _c += qe;
            i = MqDecoder.Nmps[i];
        }
        else
        {
            if (_a < qe) _c += qe;
            else _a = qe;
            if (MqDecoder.Switch[i]) mps ^= 1;
            i = MqDecoder.Nlps[i];
        }
        cx[index] = (byte)((i << 1) | mps);
        do
        {
            _a <<= 1;
            _c <<= 1;
            if (--_ct == 0) ByteOut();
        } while ((_a & 0x8000) == 0);
    }

    /// <summary>Encodes an integer (ITU-T T.88, A.2, IAx); null = OOB.</summary>
    public void EncodeInt(byte[] cx, int? value)
    {
        var prev = 1;
        void Bits(long v, int n)
        {
            for (var k = n - 1; k >= 0; k--)
            {
                var bit = (int)((v >> k) & 1);
                Encode(cx, prev, bit);
                prev = prev < 256 ? (prev << 1) | bit : (((prev << 1) | bit) & 511) | 256;
            }
        }
        if (value is not { } v)
        {
            // OOB: sign 1, value 0
            Bits(1, 1);
            Bits(0, 1);
            Bits(0, 2);
            return;
        }
        Bits(v < 0 ? 1 : 0, 1);
        long m = Math.Abs((long)v);
        if (m < 4) { Bits(0, 1); Bits(m, 2); }
        else if (m < 20) { Bits(2, 2); Bits(m - 4, 4); }
        else if (m < 84) { Bits(6, 3); Bits(m - 20, 6); }
        else if (m < 340) { Bits(14, 4); Bits(m - 84, 8); }
        else if (m < 4436) { Bits(30, 5); Bits(m - 340, 12); }
        else { Bits(31, 5); Bits(m - 4436, 32); }
    }

    /// <summary>Encodes a symbol ID (ITU-T T.88, A.3, IAID) with <paramref name="codeLength"/> bits.</summary>
    public void EncodeId(byte[] cx, int codeLength, int id)
    {
        var prev = 1;
        for (var k = codeLength - 1; k >= 0; k--)
        {
            var bit = (id >> k) & 1;
            Encode(cx, prev, bit);
            prev = (prev << 1) | bit;
        }
    }

    private void ByteOut()
    {
        if (_buffer[_bp] == 0xFF)
        {
            Put((byte)(_c >> 20));
            _c &= 0xFFFFF;
            _ct = 7;
        }
        else if (_c < 0x8000000)
        {
            Put((byte)(_c >> 19));
            _c &= 0x7FFFF;
            _ct = 8;
        }
        else
        {
            // Carry into the last byte (never at the start: the interval is still below 0x8000000 then)
            _buffer[_bp]++;
            if (_buffer[_bp] == 0xFF)
            {
                _c &= 0x7FFFFFF;
                Put((byte)(_c >> 20));
                _c &= 0xFFFFF;
                _ct = 7;
            }
            else
            {
                Put((byte)(_c >> 19));
                _c &= 0x7FFFF;
                _ct = 8;
            }
        }
    }

    private void Put(byte b)
    {
        if (++_bp == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
        _buffer[_bp] = b;
    }

    /// <summary>Flushes the encoder (ITU-T T.88, E.2.9) and appends the marker 0xFF 0xAC; returns the bytes without the placeholder.</summary>
    public byte[] Finish()
    {
        var (buffer, length) = FinishRaw();
        return buffer.AsSpan(1, length).ToArray();
    }

    /// <summary>Like <see cref="Finish"/>, without a copy: the buffer, with the data starting at index 1 and this length.</summary>
    public (byte[] Buffer, int Length) FinishRaw()
    {
        var temp = _c + _a;
        _c |= 0xFFFF;
        if (_c >= temp) _c -= 0x8000;
        _c <<= _ct;
        ByteOut();
        _c <<= _ct;
        ByteOut();
        if (_buffer[_bp] != 0xFF) Put(0xFF);
        Put(0xAC);
        return (_buffer, _bp);
    }
}

/// <summary>
/// JBIG2 encoder (see KONZEPT.md, section 5.12), always lossless: the embedded data stream for /JBIG2Decode (without file header, one page).
/// </summary>
public static class Jbig2Encoder
{
    /// <summary>
    /// Encodes one page as a generic region (ITU-T T.88, 6.2, arithmetic, template 0 with the usual AT pixels).
    /// </summary>
    /// <param name="pdfBits">Rows padded to whole bytes, 0 = black as in PDF image data (with /ImageMask: 0 paints).</param>
    /// <param name="width">Width in px.</param>
    /// <param name="height">Height in px.</param>
    /// <param name="rowBytes">Bytes per row in <paramref name="pdfBits"/> (at least the packed row length).</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <param name="tpgdon">Skip identical rows (TPGDON). Off by default: at least one widely used reader shows only noise with it (checked with the
    /// test file bitmap-tpgdon.pdf), and it saves hardly anything.</param>
    /// <returns>The segments: page information, immediate lossless generic region — no end of page: ISO 32000-2, 7.4.7 does not allow it in the
    /// embedded stream (docs/FEHLERGEDAECHTNIS.md, No. 731).</returns>
    /// <exception cref="ArgumentOutOfRangeException">The image is empty.</exception>
    /// <exception cref="ArgumentException"><paramref name="pdfBits"/> is too short.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was triggered.</exception>
    public static byte[] Generic(ReadOnlySpan<byte> pdfBits, int width, int height, int rowBytes, CancellationToken cancel = default, bool tpgdon = false)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "JBIG2: leeres Bild");
        var stride = (width + 7) / 8;
        if (rowBytes < stride || pdfBits.Length < (long)rowBytes * (height - 1) + stride) throw new ArgumentException("JBIG2: zu wenig Bilddaten");
        // Three row buffers (JBIG2: 1 = black, bits beyond the width 0) with two zero bytes of margin: the context bits come byte by byte from a
        // 24-bit window, without a bounds check per pixel and without an inverted copy of the whole image
        var lastMask = (byte)(width % 8 == 0 ? 0xFF : 0xFF << (8 - width % 8));
        byte[] current = new byte[stride + 2], above = new byte[stride + 2], above2 = new byte[stride + 2];
        var encoder = new MqEncoder();
        var cx = new byte[1 << 16];
        var ltp = 0;
        for (var y = 0; y < height; y++)
        {
            var source = pdfBits.Slice(y * rowBytes, stride);
            for (var b = 0; b < stride; b++) current[b] = (byte)~source[b];
            current[stride - 1] &= lastMask;
            // TPGDON: row equal to the one above (the first one compared with an empty row)
            if (tpgdon)
            {
                var same = y > 0 ? current.AsSpan(0, stride).SequenceEqual(above.AsSpan(0, stride)) : current.AsSpan(0, stride).IndexOfAnyExcept((byte)0) < 0;
                var sltp = (same ? 1 : 0) ^ ltp;
                encoder.Encode(cx, 0x9B25, sltp);
                ltp ^= sltp;
                if (same)
                {
                    (above2, above, current) = (above, current, above2);
                    continue;
                }
            }
            // As Jbig2Regions.NominalRow for template 0: row −2 (5 bits, up to x + 2), row −1 (7 bits, up to x + 3), current row (4 bits).
            // Initial values: row −1 at x = 0 … 3, row −2 at x = 0 … 2 (0 to the left of it)
            int w0 = 0, w1 = above[0] >> 4, w2 = above2[0] >> 5;
            for (var i = 0; i < stride; i++)
            {
                // Window from byte i: bit 23 is x = 8i
                int win1 = above[i] << 16 | above[i + 1] << 8 | above[i + 2], win2 = above2[i] << 16 | above2[i + 1] << 8 | above2[i + 2];
                int bits = current[i], end = Math.Min(8, width - i * 8);
                for (var k = 0; k < end; k++)
                {
                    var bit = (bits >> (7 - k)) & 1;
                    encoder.Encode(cx, w2 << 11 | w1 << 4 | w0, bit);
                    w0 = (w0 << 1 | bit) & 0xF;
                    w1 = (w1 << 1 | (win1 >> (19 - k)) & 1) & 0x7F;   // x + 4
                    w2 = (w2 << 1 | (win2 >> (20 - k)) & 1) & 0x1F;   // x + 3
                }
            }
            (above2, above, current) = (above, current, above2);
            if ((y & 63) == 0) cancel.ThrowIfCancellationRequested();
        }
        var (coded, length) = encoder.FinishRaw();

        // Page information, immediate lossless generic region (type 38: region info, flags (optional TPGDON, template 0,
        // arithmetic), AT pixels, data): in one array, without intermediate copies. No end-of-page segment (ISO 32000-2, 7.4.7)
        const int pageInfo = 11 + 19, regionHead = 11 + 17 + 1 + 8;
        var result = new byte[pageInfo + regionHead + length];
        Header(result, 0, 0, 48, 19);
        U32(result, 11, (uint)width);
        U32(result, 15, (uint)height);
        result[11 + 16] = 0x01;   // page "eventually lossless", default pixel white, operator OR
        var p = pageInfo;
        Header(result, p, 1, 38, 17 + 1 + 8 + length);
        U32(result, p + 11, (uint)width);
        U32(result, p + 15, (uint)height);
        result[p + 28] = (byte)(tpgdon ? 0x08 : 0x00);
        sbyte[] at = [3, -1, -3, -1, 2, -2, -2, -2];
        for (var i = 0; i < at.Length; i++) result[p + 29 + i] = (byte)at[i];
        Buffer.BlockCopy(coded, 1, result, p + regionHead, length);
        return result;
    }

    /// <summary>Writes a segment header (ITU-T T.88, 7.2): number, type, no referred segments, page 1, length.</summary>
    private static void Header(byte[] d, int p, uint number, int type, int length)
    {
        U32(d, p, number);
        d[p + 4] = (byte)type;
        d[p + 5] = 0;
        d[p + 6] = 1;
        U32(d, p + 7, (uint)length);
    }

    private static void U32(byte[] d, int p, uint v) => (d[p], d[p + 1], d[p + 2], d[p + 3]) = ((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
}
