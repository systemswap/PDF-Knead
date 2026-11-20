using System.Runtime.CompilerServices;

namespace PdfKnead.Core.Pdf.Filters.Jpeg;

/// <summary>
/// The bits of entropy-coded data (ITU-T T.81, B.1.1.5 and F.1.2.3): most significant bit first, a stuffed zero after every 0xFF data
/// byte removed, fill bytes 0xFF before a marker passed over. At a marker the data ends: from there zero bits follow, and
/// <see cref="RanShort"/> tells whether any of them were used — the scan was shorter than the image it claimed (damaged or cut off).
/// </summary>
internal sealed class JpegBits(byte[] data, int position, int end)
{
    private ulong _buffer;   // the next bits at the top
    private int _count;      // bits in the buffer
    private int _fake;       // of them the last ones that are not data (zeros after the end)

    /// <summary>The position after the last data byte read.</summary>
    public int Position { get; private set; } = position;

    /// <summary>The position of the marker (its 0xFF) that ended the data; −1 while none was reached.</summary>
    public int Marker { get; private set; } = -1;

    /// <summary>
    /// Bits after the end of the data were used: the scan claimed more than its data held. The zero bits after the end sit at the bottom of the
    /// buffer; once one is consumed, the count stays below them until the next restart (checked here, not at every consumed bit).
    /// </summary>
    public bool RanShort => _count < _fake;

    /// <summary>Bytes before a restart marker that did not belong there were passed over, or the restart markers were out of order.</summary>
    public bool Skipped { get; private set; }

    /// <summary>Fills the buffer up to at least 57 bits (zeros after the end of the data).</summary>
    private void Fill()
    {
        while (_count <= 56)
        {
            var b = 0;
            if (Marker < 0 && Position < end)
            {
                b = data[Position];
                if (b != 0xFF)
                {
                    Position++;
                }
                else
                {
                    // 0xFF: fill bytes, then 0x00 (a stuffed data byte 0xFF) or the code of a marker
                    var p = Position + 1;
                    while (p < end && data[p] == 0xFF) p++;
                    if (p < end && data[p] == 0x00)
                    {
                        Position = p + 1;
                    }
                    else
                    {
                        Marker = p < end ? p - 1 : -1;
                        Position = p < end ? p - 1 : end;
                        b = -1;
                    }
                }
            }
            else
            {
                b = -1;
            }
            if (b < 0)
            {
                // past the data: zero bits, counted as not being data
                b = 0;
                _fake += 8;
            }
            _buffer |= (ulong)b << (56 - _count);
            _count += 8;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Consume(int n)
    {
        _buffer <<= n;
        _count -= n;
    }

    /// <summary>The next <see cref="JpegHuffmanTable.FastBits"/> bits, the buffer filled to at least 16.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekFast()
    {
        if (_count < 16) Fill();
        return (int)(_buffer >> (64 - JpegHuffmanTable.FastBits));
    }

    /// <summary>Consumes <paramref name="n"/> bits already in the buffer (after <see cref="PeekFast"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Skip(int n) => Consume(n);

    /// <summary>The next <paramref name="n"/> bits (0–16) without consuming them.</summary>
    public int Peek(int n)
    {
        if (_count < n) Fill();
        return n == 0 ? 0 : (int)(_buffer >> (64 - n));
    }

    /// <summary>Reads <paramref name="n"/> bits (0–16) as an unsigned number.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Read(int n)
    {
        if (n == 0) return 0;
        if (_count < n) Fill();
        var v = (int)(_buffer >> (64 - n));
        Consume(n);
        return v;
    }

    /// <summary>One bit.</summary>
    public int Bit() => Read(1);

    /// <summary>Reads <paramref name="s"/> bits and extends them to a signed value (T.81, F.2.2.1, EXTEND).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Receive(int s)
    {
        if (s == 0) return 0;
        var v = Read(s);
        return v < 1 << (s - 1) ? v - (1 << s) + 1 : v;
    }

    /// <summary>Decodes one Huffman symbol; −1 if the bits are no code of the table (damaged data).</summary>
    public int Decode(JpegHuffmanTable table)
    {
        if (_count < 16) Fill();
        var fast = table.Fast[(int)(_buffer >> (64 - JpegHuffmanTable.FastBits))];
        if (fast != 0)
        {
            Consume(fast >> 8);
            return fast & 0xFF;
        }
        for (var length = JpegHuffmanTable.FastBits + 1; length <= 16; length++)
        {
            var code = (int)(_buffer >> (64 - length));
            if (code <= table.MaxCode[length])
            {
                Consume(length);
                return table.Symbols[table.ValueIndex[length] + code - table.MinCode[length]];
            }
        }
        // no code of this length either: consume nothing more than the shortest possible; the caller treats the rest as damaged
        return -1;
    }

    /// <summary>
    /// At the end of a restart interval (T.81, B.2.1, F.1.2.3): the bits up to the byte boundary are dropped and the restart marker
    /// <paramref name="number"/> (0–7) is consumed. Bytes before it are passed over; a restart marker with another number is taken
    /// as well (both noted in <see cref="Skipped"/>).
    /// </summary>
    /// <returns>False if another marker comes first (the scan ends there).</returns>
    public bool Restart(int number)
    {
        _buffer = 0;
        _count = 0;
        _fake = 0;
        if (Marker < 0)
        {
            var at = NextMarker(data, Position, end);
            if (at < 0)
            {
                Position = end;
                return false;
            }
            if (at != Position) Skipped = true;
            Marker = at;
        }
        var code = data[Marker + 1];
        if (code is < 0xD0 or > 0xD7)
        {
            Position = Marker;
            return false;
        }
        if (code != 0xD0 + number) Skipped = true;
        Position = Marker + 2;
        Marker = -1;
        return true;
    }

    /// <summary>Where the next marker starts after the scan: the one that ended the data, otherwise the next in the bytes after it.</summary>
    public int End() => Marker >= 0 ? Marker : NextMarker(data, Position, end) is var at and >= 0 ? at : end;

    /// <summary>The position of the next marker (0xFF followed by neither 0x00 nor 0xFF) from <paramref name="from"/> on; −1 if none.</summary>
    internal static int NextMarker(byte[] data, int from, int end)
    {
        for (var p = from; p + 1 < end; p++)
        {
            if (data[p] != 0xFF) continue;
            var q = p + 1;
            while (q < end && data[q] == 0xFF) q++;
            if (q >= end) return -1;
            if (data[q] != 0x00) return q - 1;
            p = q;
        }
        return -1;
    }
}

/// <summary>
/// A Huffman table of a DHT segment (ITU-T T.81, B.2.4.2, C and F.2.2.3): codes assigned in order of length, decoded through a table
/// of the first <see cref="FastBits"/> bits and, for longer codes, the largest code of each length.
/// </summary>
internal sealed class JpegHuffmanTable
{
    /// <summary>Bits looked up at once.</summary>
    public const int FastBits = 9;

    /// <summary>For each value of the next <see cref="FastBits"/> bits: code length × 256 + symbol; 0 if the code is longer.</summary>
    public readonly ushort[] Fast = new ushort[1 << FastBits];

    /// <summary>Per length (1–16) the largest code; −1 if there is none of that length.</summary>
    public readonly int[] MaxCode = new int[17];

    /// <summary>Per length the smallest code.</summary>
    public readonly int[] MinCode = new int[17];

    /// <summary>Per length the index of its first symbol in <see cref="Symbols"/>.</summary>
    public readonly int[] ValueIndex = new int[17];

    /// <summary>The symbols in order of their codes.</summary>
    public byte[] Symbols { get; }

    /// <summary>The largest symbol the table holds.</summary>
    public int Largest { get; }

    /// <summary>
    /// For AC tables: for each value of the next <see cref="FastBits"/> bits whose code and magnitude bits fit in them, the whole coefficient —
    /// value × 256 + run × 16 + bits used (code and magnitude); 0 otherwise (a longer code, end of block or ZRL go the ordinary way).
    /// </summary>
    public readonly int[] FastAc = new int[1 << FastBits];

    /// <summary>Builds the table; throws <see cref="JpegException"/> if the counts assign more codes than a length has room for.</summary>
    /// <param name="counts">Number of codes per length 1–16 (index 0 unused).</param>
    /// <param name="symbols">The symbols, as many as the counts add up to.</param>
    public JpegHuffmanTable(ReadOnlySpan<int> counts, byte[] symbols)
    {
        Symbols = symbols;
        var code = 0;
        var k = 0;
        for (var length = 1; length <= 16; length++)
        {
            ValueIndex[length] = k;
            MinCode[length] = code;
            for (var i = 0; i < counts[length]; i++)
            {
                // a code must fit in its length (C.2: the counts of a damaged table can promise more than there is room for)
                if (code >= 1 << length) throw new JpegException("Huffman-Tabelle ungültig");
                if (length <= FastBits)
                {
                    var shift = FastBits - length;
                    for (var j = 0; j < 1 << shift; j++) Fast[code << shift | j] = (ushort)(length << 8 | symbols[k]);
                }
                code++;
                k++;
            }
            MaxCode[length] = counts[length] > 0 ? code - 1 : -1;
            code <<= 1;
        }
        Largest = symbols.Length == 0 ? 0 : symbols.Max();
        for (var i = 0; i < Fast.Length; i++)
        {
            int length = Fast[i] >> 8, rs = Fast[i] & 0xFF;
            int run = rs >> 4, size = rs & 15;
            if (length == 0 || size == 0 || length + size > FastBits) continue;
            var magnitude = (i >> (FastBits - length - size)) & ((1 << size) - 1);
            var value = magnitude < 1 << (size - 1) ? magnitude - (1 << size) + 1 : magnitude;
            FastAc[i] = value * 256 + run * 16 + length + size;
        }
    }

    // the typical tables of ITU-T T.81, K.3 (Tables K.3–K.6): the number of codes per length 1–16, then the symbols in code order
    private static readonly byte[] DcLuminanceCounts = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DcChrominanceCounts = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static readonly byte[] DcSymbols = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static readonly byte[] AcLuminanceCounts = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D];
    private static readonly byte[] AcLuminanceSymbols =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91,
        0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A,
        0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x53,
        0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
        0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9,
        0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2,
        0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA,
    ];
    private static readonly byte[] AcChrominanceCounts = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];
    private static readonly byte[] AcChrominanceSymbols =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14,
        0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0, 0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17,
        0x18, 0x19, 0x1A, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A,
        0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78,
        0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3,
        0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7,
        0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2,
        0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA,
    ];

    private static readonly JpegHuffmanTable[] Typical =
    [
        Of(DcLuminanceCounts, DcSymbols), Of(DcChrominanceCounts, DcSymbols), Of(AcLuminanceCounts, AcLuminanceSymbols),
        Of(AcChrominanceCounts, AcChrominanceSymbols),
    ];

    /// <summary>The counts and symbols of a typical table of T.81, K.3 (for the local comparison with the norm text, JpegDecoderTests).</summary>
    internal static (byte[] Counts, byte[] Symbols)[] TypicalSpecifications =>
        [(DcLuminanceCounts, DcSymbols), (DcChrominanceCounts, DcSymbols), (AcLuminanceCounts, AcLuminanceSymbols), (AcChrominanceCounts, AcChrominanceSymbols)];

    /// <summary>
    /// The table for a scan that names a Huffman table no DHT segment defined: for DC and AC table 0 the typical luminance table of T.81,
    /// K.3, for table 1 the chrominance table; null for tables 2 and 3. Motion-JPEG frames leave these tables out by convention (they hold
    /// only the image data), and libjpeg-turbo — the codec of the display before Stufe A — fills them in the same way (S-A A102).
    /// </summary>
    public static JpegHuffmanTable? Standard(bool ac, int slot) => slot is 0 or 1 ? Typical[(ac ? 2 : 0) + slot] : null;

    private static JpegHuffmanTable Of(byte[] counts, byte[] symbols)
    {
        var c = new int[17];
        for (var l = 1; l <= 16; l++) c[l] = counts[l - 1];
        return new JpegHuffmanTable(c, symbols);
    }
}
