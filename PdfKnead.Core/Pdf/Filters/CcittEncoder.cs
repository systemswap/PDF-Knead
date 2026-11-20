namespace PdfKnead.Core.Pdf.Filters;

/// <summary>
/// Encodes CCITT Group 4 (ITU-T T.6, "MMR"), for black-and-white images when compressing. Lossless; scans typically shrink to a tenth or
/// less. Written from the standard: the two-dimensional coding of ITU-T T.4, 4.2 (the changing elements a0, a1, a2, b1, b2 of 4.2.1.2 and
/// the three modes of 4.2.1.3 with the codes of Table 4) as T.6, 2.2 uses it — every line against the line above, the first against an
/// imaginary white line, no EOL between lines, EOFB at the end. Run lengths with the codes of T.4, 4.1.2 (Tables 2 and 3), shared with the
/// decoder (<see cref="CcittFax"/>). No code of another encoder was used (the encoder taken over before was replaced on 16.11.2026;
/// docs/LIZENZPRUEFUNG.md).
/// </summary>
public static class CcittEncoder
{
    /// <summary>
    /// Encodes 1-bit rows (most significant bit first, <paramref name="rowBytes"/> per row). Black is a bit equal to 1 if
    /// <paramref name="blackIs1"/> is set, otherwise 0, matching /BlackIs1 in the /DecodeParms (/K -1, /Columns, /Rows).
    /// </summary>
    /// <param name="bits">The rows.</param>
    /// <param name="width">Width in px (/Columns).</param>
    /// <param name="height">Number of rows (/Rows).</param>
    /// <param name="rowBytes">Bytes per row in <paramref name="bits"/>.</param>
    /// <param name="blackIs1">Whether a 1 bit is black.</param>
    /// <param name="byteAlign">Pad every row to whole bytes (/EncodedByteAlign true; only for testing the decoder).</param>
    /// <returns>The encoded data, terminated by EOFB.</returns>
    public static byte[] EncodeG4(ReadOnlySpan<byte> bits, int width, int height, int rowBytes, bool blackIs1, bool byteAlign = false)
    {
        var writer = new BitWriter(Math.Max(64, bits.Length / 8));
        var reference = new byte[width];   // 1 = black; initially a white row
        var coding = new byte[width];
        for (var y = 0; y < height; y++)
        {
            var row = bits.Slice(y * rowBytes, rowBytes);
            for (var x = 0; x < width; x++)
                coding[x] = (byte)((((row[x >> 3] >> (7 - (x & 7))) & 1) == 1) == blackIs1 ? 1 : 0);
            EncodeRow(ref writer, coding, reference, width);
            if (byteAlign) writer.Align();
            (reference, coding) = (coding, reference);
        }
        // EOFB: two EOLs
        writer.Write("000000000001");
        writer.Write("000000000001");
        return writer.ToArray();
    }

    /// <summary>The codes of the vertical mode (T.4, Table 4), for a1 − b1 from −3 (VL3) to +3 (VR3).</summary>
    private static readonly string[] Vertical = ["0000010", "000010", "010", "1", "011", "000011", "0000011"];

    private const string Pass = "0001", Horizontal = "001";

    /// <summary>
    /// One coding line against its reference line (1 = black), as T.4, 4.2.1.3 describes it: a0 starts on an imaginary white changing
    /// element just before the first element. From a0: a1 the next changing element on the coding line, b1 the first changing element on
    /// the reference line to the right of a0 whose colour is opposite to that of a0, b2 the next one after b1. If b2 lies to the left of a1,
    /// pass mode, and a0 moves to below b2 (its colour stays). Otherwise, if a1 is at most three elements from b1, vertical mode, and a0
    /// moves to a1; else horizontal mode with the runs a0a1 and a1a2 (a2 the next changing element after a1), and a0 moves to a2. Beyond
    /// the line imaginary changing elements stand at its end. The line is done when a0 reaches its end.
    /// </summary>
    private static void EncodeRow(ref BitWriter writer, byte[] coding, byte[] reference, int width)
    {
        int a0 = -1, colour = 0;   // the imaginary white element before the line
        while (a0 < width)
        {
            var a1 = ChangingAfter(coding, a0, width);
            var b1 = ChangingAfter(reference, a0, width);
            while (b1 < width && reference[b1] == colour) b1 = ChangingAfter(reference, b1, width);
            var b2 = ChangingAfter(reference, b1, width);
            if (b2 < a1)
            {
                writer.Write(Pass);
                a0 = b2;
            }
            else if (Math.Abs(a1 - b1) <= 3)
            {
                writer.Write(Vertical[a1 - b1 + 3]);
                a0 = a1;
                colour ^= 1;
            }
            else
            {
                var a2 = ChangingAfter(coding, a1, width);
                writer.Write(Horizontal);
                // the run a0a1 in the colour of a0 — at the start of the line, where a0 is the imaginary element, one shorter (4.2.1.3.4)
                WriteRun(ref writer, a1 - Math.Max(a0, 0), white: colour == 0);
                WriteRun(ref writer, a2 - a1, white: colour != 0);
                a0 = a2;
            }
        }
    }

    /// <summary>
    /// The next changing element after <paramref name="position"/>: the first element whose colour differs from that of the element before
    /// it (before the first element an imaginary white one); the end of the line if there is none.
    /// </summary>
    private static int ChangingAfter(byte[] line, int position, int width)
    {
        for (var i = Math.Max(position + 1, 0); i < width; i++)
            if (line[i] != (i == 0 ? 0 : line[i - 1])) return i;
        return width;
    }

    /// <summary>
    /// A run length (T.4, 4.1.2): from 2560 on, the make-up code of 2560 as often as needed; then a make-up code for a multiple of 64 if the
    /// rest is 64 or more (Tables 2 and 3, the extended ones from 1792 on, which both colours share); then the terminating code of the rest.
    /// </summary>
    private static void WriteRun(ref BitWriter writer, int run, bool white)
    {
        var terminating = white ? CcittFax.WhiteTerminating : CcittFax.BlackTerminating;
        var makeup = white ? CcittFax.WhiteMakeup : CcittFax.BlackMakeup;
        while (run >= 2560)
        {
            writer.Write(CcittFax.ExtendedMakeup[^1]);
            run -= 2560;
        }
        if (run >= 64)
        {
            var m = run / 64 * 64;
            writer.Write(m <= 1728 ? makeup[m / 64 - 1] : CcittFax.ExtendedMakeup[(m - 1792) / 64]);
            run -= m;
        }
        writer.Write(terminating[run]);
    }

    private struct BitWriter(int capacity)
    {
        private byte[] _data = new byte[capacity];
        private int _length;
        private int _bit;   // bits used in the last byte

        public void Write(string code)
        {
            foreach (var c in code)
            {
                if (_bit == 0)
                {
                    if (_length == _data.Length) Array.Resize(ref _data, _data.Length * 2);
                    _data[_length++] = 0;
                }
                if (c == '1') _data[_length - 1] |= (byte)(0x80 >> _bit);
                _bit = (_bit + 1) & 7;
            }
        }

        /// <summary>Fills the remaining bits of the byte with zeros (/EncodedByteAlign).</summary>
        public void Align() => _bit = 0;

        public readonly byte[] ToArray() => _data[.._length];
    }
}
