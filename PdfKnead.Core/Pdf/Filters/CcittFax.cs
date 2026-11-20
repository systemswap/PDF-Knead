using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Filters;

/// <summary>
/// CCITTFaxDecode (ISO 32000-2, 7.4.6; ITU-T T.4 and T.6): Group 3 one-dimensional (K = 0),
/// Group 3 mixed one-/two-dimensional (K &gt; 0) and Group 4 (K &lt; 0). Output like an
/// unfiltered 1-bit image: rows padded to whole bytes, black = 0 (black = 1 with /BlackIs1 true).
/// Typical for scans and for logos that printer drivers embed as black-and-white images.
/// </summary>
internal static class CcittFax
{
    /// <summary>Decodes a CCITTFaxDecode stream.</summary>
    /// <param name="data">The encoded data.</param>
    /// <param name="parms">The /DecodeParms, or null for the defaults.</param>
    /// <param name="truncated">Set to true if the data is damaged or ends early; rows that could not be decoded stay white.</param>
    /// <param name="sizeHint">Expected size in bytes (image rows × row bytes); determines the row count without /Rows.</param>
    /// <returns>The image rows.</returns>
    public static byte[] Decode(byte[] data, PdfDictionary? parms, ref bool truncated, int sizeHint = 0)
    {
        var k = (int)Math.Clamp(parms?.GetInteger(PdfName.Get("K")) ?? 0, int.MinValue, int.MaxValue);
        var columns = (int)Math.Clamp(parms?.GetInteger(PdfName.Get("Columns")) ?? 1728, 1, 1 << 20);
        var rows = (int)Math.Clamp(parms?.GetInteger(PdfName.Get("Rows")) ?? 0, 0, int.MaxValue);
        var byteAlign = parms?.GetBoolean(PdfName.Get("EncodedByteAlign")) ?? false;
        var blackIs1 = parms?.GetBoolean(PdfName.Get("BlackIs1")) ?? false;
        var endOfBlock = parms?.GetBoolean(PdfName.Get("EndOfBlock")) ?? true;
        var rowBytes = (columns + 7) / 8;

        // Row count: with /EndOfBlock true (the default) the data end with EOFB or RTC, which overrides /Rows (ISO 32000-2, 7.4.6, Table 11) —
        // then up to the image height; with false, /Rows. Stopping at a smaller /Rows left the rest of the image white, where widely used
        // readers show it (docs/FEHLERGEDAECHTNIS.md, No. 729). Missing rows stay white. Every row needs at least one bit, so more rows than
        // the data has bits make no sense.
        var expected = endOfBlock
            ? sizeHint > 0 ? Math.Max(Math.Max(1, sizeHint / rowBytes), rows) : rows
            : rows > 0 ? rows : sizeHint > 0 ? Math.Max(1, sizeHint / rowBytes) : 0;
        expected = (int)Math.Min(expected, (long)data.Length * 8 + 64);
        // Image size known: do not allocate more rows than it needs (otherwise /Rows and /Columns from the file could request up to 1 GB)
        if (sizeHint > 0) expected = (int)Math.Min(expected, (long)sizeHint * 2 / rowBytes + 1);
        // Without the image size the row count comes from /Rows and the length of the data alone: start with at most 4 MB and grow with the
        // rows decoded — before, a kilobyte of data with a large /Columns and /Rows took nearly a gigabyte at once (docs/FEHLERGEDAECHTNIS.md,
        // No. 961)
        var first = sizeHint > 0 ? expected : (int)Math.Min(expected, Math.Max(16, (4 << 20) / rowBytes));
        return Run(data, 0, data.Length, k, columns, expected, byteAlign, blackIs1, ref truncated, out _, first);
    }

    /// <summary>
    /// MMR for JBIG2 (ITU-T T.88, 6.2.6): Group 4 with exactly <paramref name="height"/> rows, black = 1.
    /// A following EOFB is consumed as well; <paramref name="endByte"/> is the first byte after it
    /// (halftones: several bit planes in a row).
    /// </summary>
    internal static byte[] DecodeMmr(byte[] data, int start, int end, int width, int height, out int endByte)
    {
        var truncated = false;
        var result = Run(data, start, end, -1, width, height, false, true, ref truncated, out var endBit);
        endByte = (int)Math.Min(end, (endBit + 7) >> 3);
        return result;
    }

    private static byte[] Run(byte[] data, int start, int end, int k, int columns, int expected, bool byteAlign, bool blackIs1,
        ref bool truncated, out long endBit, int firstRows = 0)
    {
        var rowBytes = (columns + 7) / 8;
        var maxRows = expected > 0 ? expected : int.MaxValue;
        var white = blackIs1 ? (byte)0 : (byte)0xFF;
        // the rows allocated at once: those asked for (the size of the image is known), at most firstRows; the rest as they are decoded
        var size = (long)(firstRows > 0 ? firstRows : expected > 0 ? expected : 64) * rowBytes;
        StreamFilters.CheckSize(size);
        var output = new byte[size];
        output.AsSpan().Fill(white);

        var reader = new BitReader(data, start, end);
        // Changing elements (colour changes) of the reference row and the current row, each with end markers
        var reference = new int[columns + 4];
        var current = new int[columns + 4];
        reference[0] = reference[1] = reference[2] = columns;
        var count = 0;
        var row = 0;
        var previousDamaged = false;
        while (row < maxRows && !reader.AtEnd)
        {
            // Start of row: fill bits and EOL (Group 3); two EOLs in a row = end (RTC / EOFB)
            var eols = 0;
            var tag = -1;
            // Group 4 with EncodedByteAlign: align to the byte boundary first, then look for EOFB; otherwise the fill zeros plus the start
            // of the next code look like an EOL and the image ends after a few rows (test file issue16485; other readers do the same)
            if (byteAlign && k < 0 && row > 0) reader.Align();
            while (reader.TryEol())
            {
                eols++;
                if (k > 0) tag = reader.ReadBit();
            }
            if (eols >= 2 || reader.AtEnd) break;
            if (eols == 0 && byteAlign) reader.Align();
            if (k > 0 && tag < 0) tag = reader.ReadBit();

            var twoD = k < 0 || (k > 0 && tag == 0);
            var ok = twoD
                ? Decode2D(ref reader, reference, current, out count, columns)
                : Decode1D(ref reader, current, out count, columns);

            if (output.Length < (long)(row + 1) * rowBytes)
            {
                StreamFilters.CheckSize((long)output.Length * 2);
                var old = output.Length;
                Array.Resize(ref output, (int)Math.Min((long)output.Length * 2, Array.MaxLength));
                output.AsSpan(old).Fill(white);
            }
            Paint(output.AsSpan(row * rowBytes, rowBytes), current, count, columns, !blackIs1);
            row++;

            // The current row becomes the reference row
            (reference, current) = (current, reference);
            reference[count] = reference[count + 1] = reference[count + 2] = columns;

            if (ok)
            {
                previousDamaged = false;
                continue;
            }
            truncated = true;
            // Group 4 depends on the damaged row and stops
            if (k < 0) break;
            // Group 3 resynchronises at the next EOL. The damaged row is replaced by the previous row if that one was not damaged, else by a
            // white row, and the replacement is the reference row of what follows (ISO 32000-2, 7.4.6, Table 11, DamagedRowsBeforeError;
            // docs/FEHLERGEDAECHTNIS.md, No. 776) — before, the part decoded up to the fault was painted and referred to
            var damagedRow = output.AsSpan((row - 1) * rowBytes, rowBytes);
            if (row >= 2 && !previousDamaged)
            {
                output.AsSpan((row - 2) * rowBytes, rowBytes).CopyTo(damagedRow);
                (reference, current) = (current, reference);   // the changes of the previous row are the reference again
            }
            else
            {
                damagedRow.Fill(white);
                reference[0] = reference[1] = reference[2] = columns;   // an all-white reference row
            }
            previousDamaged = true;
            if (!reader.SeekEol()) break;
        }

        // An EOFB after the last row still belongs to the data
        if (row == maxRows && reader.TryEol()) reader.TryEol();
        endBit = reader.Position;

        if (expected == 0 && output.Length != row * rowBytes) Array.Resize(ref output, row * rowBytes);
        return output;
    }

    // ------------------------------------------------------------ Rows

    private static bool Decode1D(ref BitReader reader, int[] changes, out int count, int columns)
    {
        count = 0;
        var a0 = 0;
        var color = 0;   // 0 = white (every row starts white), 1 = black
        while (a0 < columns)
        {
            var run = ReadRun(ref reader, color);
            if (run < 0) return false;
            a0 += run;
            Add(changes, ref count, a0, columns);
            color ^= 1;
        }
        return true;
    }

    /// <summary>Two-dimensional coding (ITU-T T.4, 4.2 / T.6): changing elements relative to the row above.</summary>
    private static bool Decode2D(ref BitReader reader, int[] reference, int[] changes, out int count, int columns)
    {
        count = 0;
        var a0 = -1;     // imaginary white pixel before the row
        var color = 0;
        var bi = 0;
        while (a0 < columns)
        {
            // b1: first changing element of the reference row to the right of a0 with the opposite colour of a0;
            // even indices change to black, odd ones to white
            while (bi > 0 && reference[bi - 1] > a0) bi--;
            while (reference[bi] <= a0) bi++;
            if ((bi & 1) != color) bi++;
            int b1 = reference[bi], b2 = reference[bi + 1];

            var mode = ModeTable[reader.Peek(7)];
            if (mode == 0) return false;
            reader.Skip(mode & 0xF);
            switch (mode >> 4)
            {
                case Pass:
                    a0 = b2;
                    break;
                case Horizontal:
                    var run1 = ReadRun(ref reader, color);
                    var run2 = run1 < 0 ? -1 : ReadRun(ref reader, color ^ 1);
                    if (run2 < 0) return false;
                    var a1 = Math.Max(a0, 0) + run1;
                    Add(changes, ref count, a1, columns);
                    a0 = a1 + run2;
                    Add(changes, ref count, a0, columns);
                    break;
                case >= VerticalLeft3 and <= VerticalRight3:
                    a0 = Math.Max(b1 + (mode >> 4) - Vertical0, Math.Max(a0, 0));
                    Add(changes, ref count, a0, columns);
                    color ^= 1;
                    break;
                default:
                    return false;   // extensions (uncompressed mode) practically never occur in PDF files
            }
        }
        return true;
    }

    /// <summary>Appends a changing element; a second one at the same or an earlier position cancels the previous one (run of length 0).</summary>
    private static void Add(int[] changes, ref int count, int position, int columns)
    {
        if (position > columns) position = columns;
        if (count > 0 && position <= changes[count - 1])
        {
            count--;
            return;
        }
        changes[count++] = position;
    }

    /// <summary>Paints a row from the changing elements: black between an even and the following odd element.</summary>
    private static void Paint(Span<byte> row, int[] changes, int count, int columns, bool blackIsZero)
    {
        for (var i = 0; i < count; i += 2)
        {
            var from = changes[i];
            var to = i + 1 < count ? changes[i + 1] : columns;
            if (from >= to) continue;
            int first = from >> 3, last = (to - 1) >> 3;
            var firstMask = (byte)(0xFF >> (from & 7));
            var lastMask = (byte)(0xFF << (7 - ((to - 1) & 7)));
            if (first == last)
            {
                Set(ref row[first], (byte)(firstMask & lastMask), blackIsZero);
                continue;
            }
            Set(ref row[first], firstMask, blackIsZero);
            row[(first + 1)..last].Fill(blackIsZero ? (byte)0 : (byte)0xFF);
            Set(ref row[last], lastMask, blackIsZero);
        }
    }

    private static void Set(ref byte target, byte mask, bool clear)
    {
        if (clear) target &= (byte)~mask;
        else target |= mask;
    }

    /// <summary>Reads the run length of one colour: make-up codes (≥ 64) up to the terminating code; −1 for an invalid code.</summary>
    private static int ReadRun(ref BitReader reader, int color)
    {
        var table = color == 0 ? WhiteTable : BlackTable;
        var bits = color == 0 ? WhiteBits : BlackBits;
        var total = 0;
        while (true)
        {
            var entry = table[reader.Peek(bits)];
            if (entry == 0) return -1;
            reader.Skip(entry & 0xF);
            var run = entry >> 4;
            total += run;
            if (run < 64) return total;
            if (total > 1 << 20) return -1;
        }
    }

    // ------------------------------------------------------------ Bits

    private struct BitReader(byte[] data, int start, int end)
    {
        private readonly long _end = (long)end * 8;
        private long _pos = (long)start * 8;

        public readonly bool AtEnd => _pos >= _end;

        public readonly long Position => Math.Min(_pos, _end);

        /// <summary>Next <paramref name="n"/> bits (≤ 16), zeros beyond the end.</summary>
        public readonly int Peek(int n)
        {
            var i = (int)(_pos >> 3);
            var window = (uint)(Byte(i) << 16 | Byte(i + 1) << 8 | Byte(i + 2));
            return (int)((window << (int)(_pos & 7)) >> (24 - n)) & ((1 << n) - 1);
        }

        private readonly int Byte(int i) => (uint)i < (uint)end ? data[i] : 0;

        public void Skip(int n) => _pos += n;

        public int ReadBit()
        {
            var bit = Peek(1);
            _pos++;
            return bit;
        }

        public void Align() => _pos = (_pos + 7) & ~7L;

        /// <summary>
        /// Skips an EOL (at least 11 zeros, then a 1, including fill bits before it).
        /// If the zeros run to the end, they are padding and the reader is placed at the end.
        /// </summary>
        public bool TryEol()
        {
            var p = _pos;
            while (p < _end && Bit(p) == 0) p++;
            if (p >= _end)
            {
                _pos = _end;   // only zeros left: padding up to the end
                return false;
            }
            if (p - _pos < 11) return false;
            _pos = p + 1;
            return true;
        }

        /// <summary>Advances to the start of the next EOL (after a damaged row in Group 3).</summary>
        public bool SeekEol()
        {
            var zeros = 0;
            for (var p = _pos; p < _end; p++)
            {
                if (Bit(p) == 0)
                {
                    zeros++;
                    continue;
                }
                if (zeros >= 11)
                {
                    _pos = p - zeros;
                    return true;
                }
                zeros = 0;
            }
            _pos = _end;
            return false;
        }

        private readonly int Bit(long p) => (data[(int)(p >> 3)] >> (7 - (int)(p & 7))) & 1;
    }

    // ------------------------------------------------------------ Code tables (ITU-T T.4, Tables 1–4)

    private const int Pass = 1, Horizontal = 2, VerticalLeft3 = 3, Vertical0 = 6, VerticalRight3 = 9;

    private const int WhiteBits = 12, BlackBits = 13;

    // The codes come before the lookup tables because static fields are initialised in textual order

    internal static readonly string[] WhiteTerminating =
    [
        "00110101", "000111", "0111", "1000", "1011", "1100", "1110", "1111",
        "10011", "10100", "00111", "01000", "001000", "000011", "110100", "110101",
        "101010", "101011", "0100111", "0001100", "0001000", "0010111", "0000011", "0000100",
        "0101000", "0101011", "0010011", "0100100", "0011000", "00000010", "00000011", "00011010",
        "00011011", "00010010", "00010011", "00010100", "00010101", "00010110", "00010111", "00101000",
        "00101001", "00101010", "00101011", "00101100", "00101101", "00000100", "00000101", "00001010",
        "00001011", "01010010", "01010011", "01010100", "01010101", "00100100", "00100101", "01011000",
        "01011001", "01011010", "01011011", "01001010", "01001011", "00110010", "00110011", "00110100",
    ];

    /// <summary>Make-up codes for 64, 128, … 1728.</summary>
    internal static readonly string[] WhiteMakeup =
    [
        "11011", "10010", "010111", "0110111", "00110110", "00110111", "01100100", "01100101",
        "01101000", "01100111", "011001100", "011001101", "011010010", "011010011", "011010100", "011010101",
        "011010110", "011010111", "011011000", "011011001", "011011010", "011011011", "010011000", "010011001",
        "010011010", "011000", "010011011",
    ];

    internal static readonly string[] BlackTerminating =
    [
        "0000110111", "010", "11", "10", "011", "0011", "0010", "00011",
        "000101", "000100", "0000100", "0000101", "0000111", "00000100", "00000111", "000011000",
        "0000010111", "0000011000", "0000001000", "00001100111", "00001101000", "00001101100", "00000110111", "00000101000",
        "00000010111", "00000011000", "000011001010", "000011001011", "000011001100", "000011001101", "000001101000", "000001101001",
        "000001101010", "000001101011", "000011010010", "000011010011", "000011010100", "000011010101", "000011010110", "000011010111",
        "000001101100", "000001101101", "000011011010", "000011011011", "000001010100", "000001010101", "000001010110", "000001010111",
        "000001100100", "000001100101", "000001010010", "000001010011", "000000100100", "000000110111", "000000111000", "000000100111",
        "000000101000", "000001011000", "000001011001", "000000101011", "000000101100", "000001011010", "000001100110", "000001100111",
    ];

    internal static readonly string[] BlackMakeup =
    [
        "0000001111", "000011001000", "000011001001", "000001011011", "000000110011", "000000110100", "000000110101", "0000001101100",
        "0000001101101", "0000001001010", "0000001001011", "0000001001100", "0000001001101", "0000001110010", "0000001110011", "0000001110100",
        "0000001110101", "0000001110110", "0000001110111", "0000001010010", "0000001010011", "0000001010100", "0000001010101", "0000001011010",
        "0000001011011", "0000001100100", "0000001100101",
    ];

    /// <summary>Extended make-up codes for both colours: 1792, 1856, … 2560.</summary>
    internal static readonly string[] ExtendedMakeup =
    [
        "00000001000", "00000001100", "00000001101", "000000010010", "000000010011", "000000010100", "000000010101",
        "000000010110", "000000010111", "000000011100", "000000011101", "000000011110", "000000011111",
    ];

    /// <summary>Entry per possible bit sequence: (run length &lt;&lt; 4) | code length, 0 = invalid.</summary>
    private static readonly int[] WhiteTable = Build(WhiteBits, WhiteTerminating, WhiteMakeup);

    private static readonly int[] BlackTable = Build(BlackBits, BlackTerminating, BlackMakeup);

    /// <summary>Mode codes, looking 7 bits ahead: (mode &lt;&lt; 4) | code length.</summary>
    private static readonly int[] ModeTable = BuildModes();

    private static int[] Build(int bits, string[] terminating, string[] makeup)
    {
        var table = new int[1 << bits];
        for (var i = 0; i < terminating.Length; i++) Fill(table, bits, terminating[i], i);
        for (var i = 0; i < makeup.Length; i++) Fill(table, bits, makeup[i], (i + 1) * 64);
        for (var i = 0; i < ExtendedMakeup.Length; i++) Fill(table, bits, ExtendedMakeup[i], 1792 + i * 64);
        return table;
    }

    private static int[] BuildModes()
    {
        var table = new int[128];
        Fill(table, 7, "0001", Pass);
        Fill(table, 7, "001", Horizontal);
        Fill(table, 7, "1", Vertical0);
        Fill(table, 7, "011", Vertical0 + 1);
        Fill(table, 7, "000011", Vertical0 + 2);
        Fill(table, 7, "0000011", Vertical0 + 3);
        Fill(table, 7, "010", Vertical0 - 1);
        Fill(table, 7, "000010", Vertical0 - 2);
        Fill(table, 7, "0000010", Vertical0 - 3);
        return table;
    }

    /// <summary>Sets all bit sequences that start with the code to (value, length).</summary>
    private static void Fill(int[] table, int bits, string code, int value)
    {
        var prefix = Convert.ToInt32(code, 2) << (bits - code.Length);
        var span = 1 << (bits - code.Length);
        for (var i = 0; i < span; i++) table[prefix + i] = (value << 4) | code.Length;
    }
}
