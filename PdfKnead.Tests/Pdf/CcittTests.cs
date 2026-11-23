using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>CCITTFaxDecode with hand-coded rows (codes from ITU-T T.4).</summary>
public sealed class CcittTests
{
    // Row of 8 pixels: 2 white, 3 black, 3 white → 1100 0111 (black = 0)
    private const byte Row = 0b1100_0111;

    // One-dimensional: white 2 "0111", black 3 "10", white 3 "1000"
    private const string Row1D = "0111" + "10" + "1000";

    // Two-dimensional over a white row: horizontal "001" + white 2 + black 3, then V0 "1" to the end
    private const string Row2DFirst = "001" + "0111" + "10" + "1";

    // The same row again: V0 three times
    private const string Row2DSame = "111";

    private const string Eol = "000000000001";

    private static byte[] Pack(string bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
            if (bits[i] == '1') bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
        return bytes;
    }

    private static StreamFilters.DecodeResult Decode(string bits, params (string Key, PdfObject Value)[] parms)
    {
        var d = new PdfDictionary();
        d.Set(PdfName.Filter, PdfName.Get("CCITTFaxDecode"));
        d.Set(PdfName.DecodeParms, TestPdf.Dict([("Columns", PdfInteger.Of(8)), .. parms]));
        return StreamFilters.Decode(Pack(bits), d);
    }

    [Fact]
    public void Group4_EncodedByteAlign_RoundTrip()
    {
        // issue16485: G4 with /EncodedByteAlign; fill zeros plus the start of the next code (e.g. VL3 "0000010") look
        // like an EOL, and the image would end after a few rows
        const int width = 61, height = 2000, rowBytes = (width + 7) / 8;
        var random = new Random(16485);
        var bits = new byte[rowBytes * height];
        // Black bars whose edges move by up to 3 pixels from row to row (vertical modes VL3 … VR3 at the start of the row)
        var edges = new[] { 5, 12, 20, 31, 40, 52 };
        for (var y = 0; y < height; y++)
        {
            for (var e = 0; e < edges.Length; e++) edges[e] = Math.Clamp(edges[e] + random.Next(-3, 4), e * 10, e * 10 + 9);
            for (var e = 0; e + 1 < edges.Length; e += 2)
                for (var i = edges[e]; i < edges[e + 1]; i++) bits[y * rowBytes + (i >> 3)] |= (byte)(0x80 >> (i & 7));
        }
        var encoded = CcittEncoder.EncodeG4(bits, width, height, rowBytes, blackIs1: true, byteAlign: true);
        var d = new PdfDictionary();
        d.Set(PdfName.Filter, PdfName.Get("CCITTFaxDecode"));
        d.Set(PdfName.DecodeParms, TestPdf.Dict(("K", PdfInteger.Of(-1)), ("Columns", PdfInteger.Of(width)), ("Rows", PdfInteger.Of(height)),
            ("EncodedByteAlign", PdfBoolean.True), ("BlackIs1", PdfBoolean.True)));
        var decoded = StreamFilters.Decode(encoded, d).Data;
        for (var y = 0; y < height; y++)
            for (var i = 0; i < rowBytes; i++)
            {
                var mask = i == rowBytes - 1 && width % 8 != 0 ? 0xFF << (8 - width % 8) & 0xFF : 0xFF;
                Assert.True((decoded[y * rowBytes + i] & mask) == (bits[y * rowBytes + i] & mask), $"Zeile {y}, Byte {i}");
            }
    }

    [Fact]
    public void Group3_OneDimensional()
    {
        var result = Decode(Row1D + Row1D, ("K", PdfInteger.Of(0)), ("Rows", PdfInteger.Of(2)));
        Assert.Equal([Row, Row], result.Data);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Group3_WithEolAndRtc_StopsAtTheEnd()
    {
        // EOL before every row, six EOLs at the end (RTC); without /Rows
        var rtc = string.Concat(Enumerable.Repeat(Eol, 6));
        var result = Decode(Eol + Row1D + Eol + Row1D + rtc, ("K", PdfInteger.Of(0)));
        Assert.Equal([Row, Row], result.Data);
    }

    [Fact]
    public void Group4_UsesTheLineAbove_AndEndsAtEofb()
    {
        var result = Decode(Row2DFirst + Row2DSame + Eol + Eol, ("K", PdfInteger.Of(-1)));
        Assert.Equal([Row, Row], result.Data);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Group3_Mixed_ReadsTheTagBit()
    {
        // K > 0: one bit before every row, 1 = one-dimensional, 0 = two-dimensional
        var result = Decode("1" + Row1D + "0" + Row2DSame, ("K", PdfInteger.Of(1)), ("Rows", PdfInteger.Of(2)));
        Assert.Equal([Row, Row], result.Data);
    }

    /// <summary>
    /// ITU-T T.4, 4.2.3: fill bits — zeros of any number — may stand before every EOL; the rows are the same as without them, one-dimensional
    /// (/K 0) and mixed (/K > 0, the tag bit after the EOL). Before no test had more than the eleven zeros of the EOL (Bericht M2, Zeile 222
    /// aufgeteilt, 20.11.2026).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(29)]
    public void Group3_FillBitsBeforeTheEol_SameRows(int fill)
    {
        var eol = new string('0', fill) + Eol;
        var rtc = string.Concat(Enumerable.Repeat(Eol, 6));
        Assert.Equal([Row, Row], Decode(eol + Row1D + eol + Row1D + rtc, ("K", PdfInteger.Of(0))).Data);
        var mixed = Decode(eol + "1" + Row1D + eol + "0" + Row2DSame, ("K", PdfInteger.Of(2)), ("EndOfLine", PdfBoolean.True),
            ("Rows", PdfInteger.Of(2)), ("EndOfBlock", PdfBoolean.False));
        Assert.Equal([Row, Row], mixed.Data);
    }

    /// <summary>
    /// ITU-T T.4, 4.2.1.1: with /K 4 the tag bit of every row decides — a one-dimensional row may come before K rows are over, as in the
    /// order one-, two-, one-, two-dimensional here.
    /// </summary>
    [Fact]
    public void Group3_KFour_OneDimensionalRowBeforeKRowsAreOver()
    {
        var result = Decode("1" + Row1D + "0" + Row2DSame + "1" + Row1D + "0" + Row2DSame, ("K", PdfInteger.Of(4)), ("Rows", PdfInteger.Of(4)),
            ("EndOfBlock", PdfBoolean.False));
        Assert.Equal([Row, Row, Row, Row], result.Data);
    }

    /// <summary>
    /// ITU-T T.4, 4.2.1.3.3 and T.6, 2.2.4: the end of a row may be coded as a1 or as a2 — here in horizontal mode as a2: white 2, black 6
    /// (black run 6 is „0010“) ends the row of 8 columns; read as 1100 0000; Group 4 and the two-dimensional row of Group 3 (/K 2, after a
    /// one-dimensional row with the same runs). The end as a1 in vertical mode: Group4_UsesTheLineAbove_AndEndsAtEofb.
    /// </summary>
    [Fact]
    public void RowEndAsA2InHorizontalMode_Group3AndGroup4()
    {
        var result = Decode("001" + "0111" + "0010" + Eol + Eol, ("K", PdfInteger.Of(-1)));
        Assert.Equal([(byte)0b1100_0000], result.Data);
        Assert.False(result.Truncated);
        var group3 = Decode("1" + "0111" + "0010" + "0" + "001" + "0111" + "0010", ("K", PdfInteger.Of(2)), ("Rows", PdfInteger.Of(2)),
            ("EndOfBlock", PdfBoolean.False));
        Assert.Equal([(byte)0b1100_0000, (byte)0b1100_0000], group3.Data);
        Assert.False(group3.Truncated);
    }

    /// <summary>ISO 32000-2, 7.4.6, Table 11: negative, zero and positive K are told apart, positive values among themselves not.</summary>
    [Fact]
    public void PositiveK_AllTheSame()
    {
        var one = Decode("1" + Row1D + "0" + Row2DSame, ("K", PdfInteger.Of(1)), ("Rows", PdfInteger.Of(2)));
        var seven = Decode("1" + Row1D + "0" + Row2DSame, ("K", PdfInteger.Of(7)), ("Rows", PdfInteger.Of(2)));
        Assert.Equal(one.Data, seven.Data);
    }

    /// <summary>
    /// ISO 32000-2, 7.4.6, Table 11: with /EndOfBlock true (the default) the data end with EOFB, which overrides /Rows — the image is read to
    /// its height; with false the filter stops after /Rows (docs/FEHLERGEDAECHTNIS.md, No. 729: rows below a too small /Rows stayed white).
    /// </summary>
    [Fact]
    public void EndOfBlock_OverridesRows()
    {
        var data = Pack(Row2DFirst + Row2DSame + Row2DSame + Eol + Eol);
        PdfDictionary Parms(bool endOfBlock)
        {
            var d = new PdfDictionary();
            d.Set(PdfName.Filter, PdfName.Get("CCITTFaxDecode"));
            d.Set(PdfName.DecodeParms, TestPdf.Dict(("K", PdfInteger.Of(-1)), ("Columns", PdfInteger.Of(8)), ("Rows", PdfInteger.Of(1)),
                ("EndOfBlock", endOfBlock ? PdfBoolean.True : PdfBoolean.False)));
            return d;
        }
        Assert.Equal([Row, Row, Row], StreamFilters.Decode(data, Parms(true), sizeHint: 3).Data);
        Assert.Equal(Row, StreamFilters.Decode(data, Parms(false), sizeHint: 3).Data[0]);
        Assert.All(StreamFilters.Decode(data, Parms(false), sizeHint: 3).Data.Skip(1), b => Assert.Equal(0xFF, b));
    }

    /// <summary>
    /// ISO 32000-2, 7.4.6: without /DamagedRowsBeforeError a damaged row would be an error. PDF-Knead finds the next EOL and reads on (Group 3),
    /// as widely used readers do (sonderregeln.md, A72); the stream counts as damaged.
    /// </summary>
    [Fact]
    public void Group3_DamagedRow_ReadsOnAtTheNextEol()
    {
        // the second row: white 2, then eight zeros — no black code starts so
        var result = Decode(Eol + Row1D + Eol + "0111" + "00000000" + Eol + Row1D, ("K", PdfInteger.Of(0)), ("Rows", PdfInteger.Of(3)));
        Assert.True(result.Truncated);
        Assert.Equal(Row, result.Data[0]);
        Assert.Equal(Row, result.Data[2]);
    }

    /// <summary>
    /// ISO 32000-2, 7.4.6, Table 11 (DamagedRowsBeforeError): a tolerated damaged row is found up to the next EOL and replaced by the previous
    /// row if that one was not damaged, else by a white row — and the next two-dimensional row refers to what replaced it. Before, the part of
    /// the damaged row decoded up to the fault was painted and served as the reference row (docs/FEHLERGEDAECHTNIS.md, No. 776).
    /// </summary>
    [Fact]
    public void Group3_DamagedRows_PreviousRowOrWhite()
    {
        const string damaged = "0111" + "00000000";   // white 2, then no code
        var oneD = Decode(Eol + Row1D + Eol + damaged + Eol + damaged + Eol + Row1D, ("K", PdfInteger.Of(0)), ("Rows", PdfInteger.Of(4)));
        Assert.True(oneD.Truncated);
        Assert.Equal(new byte[] { Row, Row, 0xFF, Row }, oneD.Data);
        // mixed (K 2): the tag bit after each EOL — 1 one-dimensional, 0 two-dimensional; the 2-D row after the damaged one repeats its reference
        var mixed = Decode(Eol + "1" + Row1D + Eol + "1" + damaged + Eol + "0" + Row2DSame, ("K", PdfInteger.Of(2)), ("Rows", PdfInteger.Of(3)));
        Assert.Equal(new byte[] { Row, Row, Row }, mixed.Data);
    }

    [Fact]
    public void BlackIs1_InvertsTheOutput()
    {
        var result = Decode(Row2DFirst, ("K", PdfInteger.Of(-1)), ("Rows", PdfInteger.Of(1)), ("BlackIs1", PdfBoolean.True));
        Assert.Equal([0b0011_1000], result.Data);
    }

    [Fact]
    public void EncodedByteAlign_SkipsFillBits()
    {
        // Every row starts on a byte boundary: 10 bits of row data + 6 fill bits
        var result = Decode(Row2DFirst + "000000" + Row2DSame, ("K", PdfInteger.Of(-1)), ("Rows", PdfInteger.Of(2)), ("EncodedByteAlign", PdfBoolean.True));
        Assert.Equal([Row, Row], result.Data);
    }

    [Fact]
    public void BrokenData_IsReportedAndRestStaysWhite()
    {
        // The second row is nonsense (seven zeros and a 1 are not a mode code)
        var result = Decode(Row2DFirst + "00000001" + "1111", ("K", PdfInteger.Of(-1)), ("Rows", PdfInteger.Of(3)));
        Assert.True(result.Truncated);
        Assert.Equal(Row, result.Data[0]);
        Assert.Equal(0xFF, result.Data[2]);
    }

    // Tables 1/T.4 (terminating codes, run lengths 0–63) and 2/T.4 (make-up codes, 64–1728, and the extended ones from 1792 to 2560 that both
    // colours share), as T.4, 4.1.2 and TIFF 6.0, section 10 give them: data of the tables, one code word per run length
    private static readonly string[] WhiteTerminatingOfT4 =
    [
        "00110101", "000111", "0111", "1000", "1011", "1100", "1110", "1111", "10011", "10100", "00111", "01000", "001000", "000011", "110100",
        "110101", "101010", "101011", "0100111", "0001100", "0001000", "0010111", "0000011", "0000100", "0101000", "0101011", "0010011", "0100100",
        "0011000", "00000010", "00000011", "00011010", "00011011", "00010010", "00010011", "00010100", "00010101", "00010110", "00010111",
        "00101000", "00101001", "00101010", "00101011", "00101100", "00101101", "00000100", "00000101", "00001010", "00001011", "01010010",
        "01010011", "01010100", "01010101", "00100100", "00100101", "01011000", "01011001", "01011010", "01011011", "01001010", "01001011",
        "00110010", "00110011", "00110100"
    ];

    private static readonly string[] BlackTerminatingOfT4 =
    [
        "0000110111", "010", "11", "10", "011", "0011", "0010", "00011", "000101", "000100", "0000100", "0000101", "0000111", "00000100", "00000111",
        "000011000", "0000010111", "0000011000", "0000001000", "00001100111", "00001101000", "00001101100", "00000110111", "00000101000",
        "00000010111", "00000011000", "000011001010", "000011001011", "000011001100", "000011001101", "000001101000", "000001101001", "000001101010",
        "000001101011", "000011010010", "000011010011", "000011010100", "000011010101", "000011010110", "000011010111", "000001101100",
        "000001101101", "000011011010", "000011011011", "000001010100", "000001010101", "000001010110", "000001010111", "000001100100",
        "000001100101", "000001010010", "000001010011", "000000100100", "000000110111", "000000111000", "000000100111", "000000101000",
        "000001011000", "000001011001", "000000101011", "000000101100", "000001011010", "000001100110", "000001100111"
    ];

    private static readonly string[] WhiteMakeupOfT4 =
    [
        "11011", "10010", "010111", "0110111", "00110110", "00110111", "01100100", "01100101", "01101000", "01100111", "011001100", "011001101",
        "011010010", "011010011", "011010100", "011010101", "011010110", "011010111", "011011000", "011011001", "011011010", "011011011",
        "010011000", "010011001", "010011010", "011000", "010011011"
    ];

    private static readonly string[] BlackMakeupOfT4 =
    [
        "0000001111", "000011001000", "000011001001", "000001011011", "000000110011", "000000110100", "000000110101", "0000001101100",
        "0000001101101", "0000001001010", "0000001001011", "0000001001100", "0000001001101", "0000001110010", "0000001110011", "0000001110100",
        "0000001110101", "0000001110110", "0000001110111", "0000001010010", "0000001010011", "0000001010100", "0000001010101", "0000001011010",
        "0000001011011", "0000001100100", "0000001100101"
    ];

    private static readonly string[] ExtendedMakeupOfT4 =
    [
        "00000001000", "00000001100", "00000001101", "000000010010", "000000010011", "000000010100", "000000010101", "000000010110", "000000010111",
        "000000011100", "000000011101", "000000011110", "000000011111"
    ];

    /// <summary>
    /// T.4, 4.1.2 and TIFF 6.0, section 10: every code word of the run-length tables — white and black terminating codes 0–63, make-up codes
    /// 64–1728 per colour, the extended make-up codes 1792–2560 — is the one the reader and the Group 4 writer use (each entry of the tables a
    /// check of its own).
    /// </summary>
    [Fact]
    public void CodeTables_AsTables1And2OfT4()
    {
        Assert.Equal(WhiteTerminatingOfT4, CcittFax.WhiteTerminating);
        Assert.Equal(BlackTerminatingOfT4, CcittFax.BlackTerminating);
        Assert.Equal(WhiteMakeupOfT4, CcittFax.WhiteMakeup);
        Assert.Equal(BlackMakeupOfT4, CcittFax.BlackMakeup);
        Assert.Equal(ExtendedMakeupOfT4, CcittFax.ExtendedMakeup);
    }

    /// <summary>
    /// T.4, 4.1.1 and TIFF 6.0, section 10: a run of 2 560 pixels or more is coded first with the make-up code of 2 560, then with a make-up code
    /// for the multiple of 64 in the rest, then the terminating code. One row of 2 720 pixels — 2 700 white, 10 black, 10 white — over the
    /// imaginary white reference row of Group 4: horizontal mode (001), white 2 700 as 2560 + 128 + 12, black 10.
    /// </summary>
    [Fact]
    public void Group4_LongRun_MakeUp2560ThenMakeUpThenTerminating()
    {
        const int width = 2720;
        var row = new byte[width / 8];
        for (var x = 2700; x < 2710; x++) row[x / 8] |= (byte)(0x80 >> (x % 8));
        var packed = CcittEncoder.EncodeG4(row, width, 1, row.Length, blackIs1: true);
        var bits = string.Concat(packed.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
        var expected = "001" + ExtendedMakeupOfT4[^1] + WhiteMakeupOfT4[1] + WhiteTerminatingOfT4[12] + BlackTerminatingOfT4[10];
        Assert.StartsWith(expected, bits);
    }

    /// <summary>
    /// T.4, 4.2.1.3.3, Table 4: the two-dimensional codes — pass 0001, horizontal 001 + M(a0a1) + M(a1a2), vertical V(0) 1, VR(1) 011, VR(2)
    /// 000011, VR(3) 0000011, VL(1) 010, VL(2) 000010, VL(3) 0000010 (each entry of the table). The writer's vertical codes in their order
    /// VL(3) … VR(3); and a stream built by hand, 8 pixels wide, Group 4: row 1 horizontal (white 2, black 2) then V(0) — pixels 2 and 3
    /// black; row 2 all white over it: b2 lies left of a1, so pass mode, then V(0). The reader gives these rows.
    /// </summary>
    [Fact]
    public void TwoDimensionalCodes_AsTable4()
    {
        var vertical = (string[])typeof(CcittEncoder).GetField("Vertical", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        Assert.Equal(["0000010", "000010", "010", "1", "011", "000011", "0000011"], vertical);
        var bits = "001" + WhiteTerminatingOfT4[2] + BlackTerminatingOfT4[2] + "1" + "0001" + "1";
        bits = bits.PadRight((bits.Length + 7) / 8 * 8, '0');
        var data = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var rows = CcittFax.DecodeMmr(data, 0, data.Length, 8, 2, out _);
        Assert.Equal(new byte[] { 0b0011_0000, 0b0000_0000 }, rows[..2]);
    }

    /// <summary>The bits of a code string as bytes, filled with zeros to a whole byte.</summary>
    private static byte[] BitsToBytes(string bits)
    {
        bits = bits.PadRight((bits.Length + 7) / 8 * 8, '0');
        return [.. Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2))];
    }

    /// <summary>
    /// T.4, 4.2.1.3.3, Table 5 (decided by the user on 23.11.2026): the uncompressed mode. One-dimensional, 16 pixels: entrance 000000001111,
    /// then 1 (black), 01 (white, black), 000001 (five white), 00001 (four white, black), exit 00000001 with one white pixel and tag 0 (white
    /// next), then the runs white 1 and black 1 — the row black, white, black, nine white, black, two white, black. Two-dimensional (Group 4),
    /// 8 pixels: entrance 0000001111, then patterns and an exit as below. Before, the row was
    /// damaged. (Two-dimensional since the probe of 23.11.2026: 01, 1, 0001, exit 00000001 with one white pixel and tag 0 — white, black,
    /// black, three white, black, white; the exit fills the row.)
    /// </summary>
    [Fact]
    public void UncompressedMode_AsTable5_OneAndTwoDimensional()
    {
        var oneD = BitsToBytes("000000001111" + "1" + "01" + "000001" + "00001" + "00000001" + "0" + WhiteTerminatingOfT4[1] + BlackTerminatingOfT4[1]);
        var truncated = false;
        var row = CcittFax.Decode(oneD, TestPdf.Dict(("K", PdfInteger.Of(0)), ("Columns", PdfInteger.Of(16)), ("Rows", PdfInteger.Of(1)),
            ("BlackIs1", PdfBoolean.True)), ref truncated);
        Assert.False(truncated);
        Assert.Equal(new byte[] { 0b1010_0000, 0b0000_1001 }, row[..2]);

        var twoD = BitsToBytes("0000001111" + "01" + "1" + "0001" + "00000001" + "0");
        var rows = CcittFax.DecodeMmr(twoD, 0, twoD.Length, 8, 1, out _);
        Assert.Equal(0b0110_0010, rows[0]);
    }

    /// <summary>
    /// T.4, Table 5 (T.6, Table 4) entry by entry, one-dimensional on 8 pixels: each pattern code (then the exit 0000001), each exit code with
    /// tag 0 and 1; the rest of the row is one run in the colour of the tag — so the tag decides, also where it equals the last pixel.
    /// </summary>
    [Theory]
    [InlineData("1", "1", 0)]
    [InlineData("1", "1", 1)]
    [InlineData("01", "01", 0)]
    [InlineData("001", "001", 0)]
    [InlineData("0001", "0001", 1)]
    [InlineData("00001", "00001", 0)]
    [InlineData("000001", "00000", 0)]
    [InlineData("000001", "00000", 1)]
    [InlineData("0000001", "", 0)]
    [InlineData("0000001", "", 1)]
    [InlineData("00000001", "0", 0)]
    [InlineData("00000001", "0", 1)]
    [InlineData("000000001", "00", 0)]
    [InlineData("000000001", "00", 1)]
    [InlineData("0000000001", "000", 0)]
    [InlineData("0000000001", "000", 1)]
    [InlineData("00000000001", "0000", 0)]
    [InlineData("00000000001", "0000", 1)]
    public void UncompressedMode_EachCodeOfTable5(string code, string pixels, int tag)
    {
        var exit = code.StartsWith("000000", StringComparison.Ordinal);
        var rest = 8 - pixels.Length;
        var run = tag == 0 ? WhiteTerminatingOfT4[rest] : BlackTerminatingOfT4[rest];
        var bits = "000000001111" + code + (exit ? "" : "0000001") + tag + run;
        var truncated = false;
        var row = CcittFax.Decode(BitsToBytes(bits), TestPdf.Dict(("K", PdfInteger.Of(0)), ("Columns", PdfInteger.Of(8)), ("Rows", PdfInteger.Of(1)),
            ("BlackIs1", PdfBoolean.True)), ref truncated);
        Assert.False(truncated);
        Assert.Equal(Convert.ToByte(pixels + new string(tag == 1 ? '1' : '0', rest), 2), row[0]);
    }

    /// <summary>
    /// T.4, Table 5: the tag bit in a two-dimensional row (Group 4, 8 pixels). Tag 0 after a black pixel: 01, exit 0000001 0, then H with white
    /// 2, black 4 — white, black, white, white, four black. Tag 1 after a white pixel: 001, exit 00000001 1 (one white), then H with black 2,
    /// white 2 — white, white, black, white, black, black, white, white.
    /// </summary>
    [Fact]
    public void UncompressedMode_TagBitInATwoDimensionalRow_ColourOfTheNextRun()
    {
        var white = BitsToBytes("0000001111" + "01" + "0000001" + "0" + "001" + WhiteTerminatingOfT4[2] + BlackTerminatingOfT4[4]);
        Assert.Equal(0b0100_1111, CcittFax.DecodeMmr(white, 0, white.Length, 8, 1, out _)[0]);
        var black = BitsToBytes("0000001111" + "001" + "00000001" + "1" + "001" + BlackTerminatingOfT4[2] + WhiteTerminatingOfT4[2]);
        Assert.Equal(0b0010_1100, CcittFax.DecodeMmr(black, 0, black.Length, 8, 1, out _)[0]);
    }

    /// <summary>
    /// T.4, Probe 23.11.2026, B1: an EOL inside the uncompressed mode ends the row without being taken, so Group 3 resynchronises at it. 4
    /// pixels, /K 0: row 1 enters the mode, one black pixel, EOL — damaged, a white row; row 2 white 0, black 4; row 3 in the mode 01, 01 and
    /// the EOL right after — the mode filled the row, so it is complete. Before, the EOL of row 1 was taken and row 2 was lost.
    /// </summary>
    [Fact]
    public void UncompressedMode_EolInsideTheMode_ResynchronisesAtIt()
    {
        var bits = Eol + "000000001111" + "1" + Eol + WhiteTerminatingOfT4[0] + BlackTerminatingOfT4[4] + Eol + "000000001111" + "01" + "01" + Eol;
        var truncated = false;
        var rows = CcittFax.Decode(BitsToBytes(bits), TestPdf.Dict(("K", PdfInteger.Of(0)), ("Columns", PdfInteger.Of(4)), ("Rows", PdfInteger.Of(3)),
            ("BlackIs1", PdfBoolean.True)), ref truncated);
        Assert.True(truncated);
        Assert.Equal(new byte[] { 0x00, 0xF0, 0x50 }, rows[..3]);
    }

    /// <summary>
    /// T.4, Table 4 (T.6, Table 1): every vertical code read by the decoder. Row 1 white 4, black 4 (H); row 2 the code under test, then V(0):
    /// black from 4 + d on.
    /// </summary>
    [Theory]
    [InlineData("1", 0)]
    [InlineData("011", 1)]
    [InlineData("000011", 2)]
    [InlineData("0000011", 3)]
    [InlineData("010", -1)]
    [InlineData("000010", -2)]
    [InlineData("0000010", -3)]
    public void VerticalCodes_DecodedAsTable4(string code, int d)
    {
        var data = BitsToBytes("001" + WhiteTerminatingOfT4[4] + BlackTerminatingOfT4[4] + code + "1");
        var rows = CcittFax.DecodeMmr(data, 0, data.Length, 8, 2, out _);
        Assert.Equal(0b0000_1111, rows[0]);
        Assert.Equal((byte)(0xFF >> (4 + d)), rows[1]);
    }

    /// <summary>T.4, 4.2.4: RTC is six EOL each with its tag bit 1 when /K &gt; 0; the reading ends there, a row after it is not read.</summary>
    [Fact]
    public void Group3_Mixed_RtcWithTagBits_StopsAtTheEnd()
    {
        var rtc = string.Concat(Enumerable.Repeat(Eol + "1", 6));
        var result = Decode(Eol + "1" + Row1D + Eol + "0" + Row2DSame + rtc + Eol + "1" + Row1D, ("K", PdfInteger.Of(2)));
        Assert.Equal([Row, Row], result.Data);
    }

    /// <summary>T.4, 4.1.1: a one-dimensional row always starts with a white run — of length 0 where the row starts black (black 3, white 5).</summary>
    [Fact]
    public void Group3_OneDimensional_RowStartingBlack_WhiteRunOfZeroFirst()
    {
        var result = Decode(WhiteTerminatingOfT4[0] + BlackTerminatingOfT4[3] + WhiteTerminatingOfT4[5], ("K", PdfInteger.Of(0)), ("Rows", PdfInteger.Of(1)));
        Assert.Equal([(byte)0b0001_1111], result.Data);
    }

    /// <summary>
    /// T.6, 2.3.1 (FG 1020, Nachtrag): in Group 4 the last pixels of a row and the first of the next form one pattern of the uncompressed mode.
    /// 4 pixels a row: entrance, 1 (black), 01 (white, black), 0001 (three white, black) — one white fits, two white and the black go on in
    /// row 2; then exit 0000001 with tag 0 and V(0) to the end: rows black, white, black, white and white, white, black, white. In Group 3 a row
    /// ends with its EOL, so the same overflow there is a damaged row.
    /// </summary>
    [Fact]
    public void UncompressedMode_RunsOnIntoTheNextRow_Group4()
    {
        var data = BitsToBytes("0000001111" + "1" + "01" + "0001" + "0000001" + "0" + "1");
        var rows = CcittFax.DecodeMmr(data, 0, data.Length, 4, 2, out _);
        Assert.Equal(0b1010_0000, rows[0]);
        Assert.Equal(0b0010_0000, rows[1]);

        var oneD = BitsToBytes("000000001111" + "0001");
        var truncated = false;
        CcittFax.Decode(oneD, TestPdf.Dict(("K", PdfInteger.Of(0)), ("Columns", PdfInteger.Of(2)), ("Rows", PdfInteger.Of(1))), ref truncated);
        Assert.True(truncated);
    }

    /// <summary>
    /// T.6, 2.2.3.1 (T.4, 4.2.1.3.2 alike): b2 just above a1 is not a pass. Row 1 black at 2 and 3 (H, white 2, black 2, then V(0)); on row 2,
    /// black from 4, b1 = 2 and b2 = 4 = a1: the encoder writes VR(2) 000011, not the pass 0001, and the rows come back unchanged.
    /// </summary>
    [Fact]
    public void PassMode_NotWhenB2JustAboveA1()
    {
        var bits = new byte[] { 0b0011_0000, 0b0000_1111 };
        var encoded = CcittEncoder.EncodeG4(bits, 8, 2, 1, blackIs1: true);
        var code = string.Concat(encoded.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
        Assert.StartsWith("001" + WhiteTerminatingOfT4[2] + BlackTerminatingOfT4[2] + "1" + "000011", code);
        Assert.Equal(bits, CcittFax.DecodeMmr(encoded, 0, encoded.Length, 8, 2, out _)[..2]);
    }

    /// <summary>
    /// Probe T.6, B1 (an own decision, 23.11.2026): with /EncodedByteAlign a pattern of the uncompressed mode running over the row end is not
    /// aligned in between. 4 pixels: 1, 01, 0001 (white, white, black go on), then 1 fills row 2 — black, white, black, white and white, white,
    /// black, black. Aligning after the 17 bits of row 1 would skip the codes of row 2 and leave it damaged.
    /// </summary>
    [Fact]
    public void UncompressedMode_RunsOnWithoutAlignment_EncodedByteAlign()
    {
        var data = BitsToBytes("0000001111" + "1" + "01" + "0001" + "1" + "01" + "1" + "001");
        var truncated = false;
        var rows = CcittFax.Decode(data, TestPdf.Dict(("K", PdfInteger.Of(-1)), ("Columns", PdfInteger.Of(4)), ("Rows", PdfInteger.Of(2)),
            ("EncodedByteAlign", PdfBoolean.True), ("BlackIs1", PdfBoolean.True)), ref truncated);
        Assert.False(truncated);
        Assert.Equal(new byte[] { 0b1010_0000, 0b0011_0000 }, rows[..2]);
    }

    /// <summary>
    /// Probe T.6, B11 (an own decision, 23.11.2026): the tag bit gives the colour of the run that starts where the exit leaves the mode. 4 pixels,
    /// Group 4. A row the mode filled (1, 001), the next row exits at once with two white pixels and tag 1 (000000001 1), then V(0): rows black,
    /// white, white, black and white, white, black, black. An exit whose white pixels run over the row end (1, 01, 0000000001 1: one white fits,
    /// two go on), then V(0) twice (b1 lies where row 1 turns white): rows black, white, black, white and white, white, black, white. Where an
    /// exit ends exactly at the row end, its
    /// tag has no run in that row and the next row starts as T.6, 2.2.5.1.
    /// </summary>
    [Fact]
    public void UncompressedMode_ExitOverTheRowEnd_TagInTheNextRow()
    {
        var filled = BitsToBytes("0000001111" + "1" + "001" + "000000001" + "1" + "1");
        Assert.Equal(new byte[] { 0b1001_0000, 0b0011_0000 }, CcittFax.DecodeMmr(filled, 0, filled.Length, 4, 2, out _)[..2]);
        var over = BitsToBytes("0000001111" + "1" + "01" + "0000000001" + "1" + "1" + "1");
        Assert.Equal(new byte[] { 0b1010_0000, 0b0010_0000 }, CcittFax.DecodeMmr(over, 0, over.Length, 4, 2, out _)[..2]);
    }

    /// <summary>
    /// Probe T.6, B11: data that end on a byte boundary right after a pattern running over the row end — the carried pixels still come into the
    /// next row, which they fill, so both rows are complete. 3 pixels, Group 4: 1, 1, 0001 (one white fits; white, white, black go on) in 16
    /// bits. Before, the carried pixels were lost (a white row).
    /// </summary>
    [Fact]
    public void UncompressedMode_DataEndingAfterTheRowEnd_CarriedPixelsKept()
    {
        var data = BitsToBytes("0000001111" + "1" + "1" + "0001");
        Assert.Equal(2, data.Length);
        var truncated = false;
        var rows = CcittFax.Decode(data, TestPdf.Dict(("K", PdfInteger.Of(-1)), ("Columns", PdfInteger.Of(3)), ("Rows", PdfInteger.Of(2)),
            ("BlackIs1", PdfBoolean.True)), ref truncated);
        Assert.False(truncated);
        Assert.Equal(new byte[] { 0b1100_0000, 0b0010_0000 }, rows[..2]);
    }

    /// <summary>
    /// Probe T.6, B11: EOFB right after a row the uncompressed mode filled ends the data there (no extra white row, no damage); with the number of
    /// rows known, an exit code without pixels after the last row still belongs to the data (the end lies behind it).
    /// </summary>
    [Fact]
    public void UncompressedMode_FilledLastRow_EofbOrExitBelongToIt()
    {
        var withEofb = BitsToBytes("0000001111" + "1" + "1" + Eol + Eol);
        var truncated = false;
        var rows = CcittFax.Decode(withEofb, TestPdf.Dict(("K", PdfInteger.Of(-1)), ("Columns", PdfInteger.Of(2)), ("BlackIs1", PdfBoolean.True)),
            ref truncated);
        Assert.False(truncated);
        Assert.Equal(new byte[] { 0b1100_0000 }, rows);
        var withExit = BitsToBytes("0000001111" + "1" + "1" + "0000001" + "0");
        Assert.Equal(3, withExit.Length);
        CcittFax.DecodeMmr(withExit, 0, withExit.Length, 2, 1, out var endByte);
        Assert.Equal(3, endByte);
    }

    /// <summary>
    /// T.6, 2.2.5.1 (T.4, 4.2.1.3.4): a two-dimensional row that starts black in the horizontal mode first codes a white run of length 0 —
    /// black 3, white 5 as H, white 0, black 3, then V(0); the encoder writes it so and the decoder reads it back.
    /// </summary>
    [Fact]
    public void Group4_RowStartingBlack_WhiteRunOfZeroFirst()
    {
        var bits = "001" + WhiteTerminatingOfT4[0] + BlackTerminatingOfT4[3] + "1";
        var data = BitsToBytes(bits);
        Assert.Equal(0b1110_0000, CcittFax.DecodeMmr(data, 0, data.Length, 8, 1, out _)[0]);
        var encoded = CcittEncoder.EncodeG4([0b1110_0000], 8, 1, 1, blackIs1: true);
        Assert.StartsWith(bits, string.Concat(encoded.Select(b => Convert.ToString(b, 2).PadLeft(8, '0'))));
    }

    /// <summary>
    /// T.6, 2.2.4 (Step 2, runs from 2624 on): a white run of 5200 is the make-up code of 2560 twice, then 64 and the terminating code of 16 —
    /// written so by the encoder and read back by the decoder (5300 pixels: white 5200, black 100).
    /// </summary>
    [Fact]
    public void Group4_RunOf5200_MakeUp2560Twice()
    {
        const int width = 5300;
        var row = new byte[(width + 7) / 8];
        for (var x = 5200; x < width; x++) row[x >> 3] |= (byte)(0x80 >> (x & 7));
        var encoded = CcittEncoder.EncodeG4(row, width, 1, row.Length, blackIs1: true);
        var bits = string.Concat(encoded.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
        Assert.StartsWith("001" + ExtendedMakeupOfT4[^1] + ExtendedMakeupOfT4[^1] + WhiteMakeupOfT4[0] + WhiteTerminatingOfT4[16]
            + BlackMakeupOfT4[0] + BlackTerminatingOfT4[36], bits);
        Assert.Equal(row, CcittFax.DecodeMmr(encoded, 0, encoded.Length, width, 1, out _)[..row.Length]);
    }

    /// <summary>T.6, 2.4.1.3 (T.4, Table 4): an extension other than 111 is not defined — the row is damaged (Group 4 stops there).</summary>
    [Fact]
    public void UndefinedExtension_DamagedRow()
    {
        var truncated = false;
        CcittFax.Decode(BitsToBytes("0000001000"), TestPdf.Dict(("K", PdfInteger.Of(-1)), ("Columns", PdfInteger.Of(8)), ("Rows", PdfInteger.Of(1))),
            ref truncated);
        Assert.True(truncated);
    }

    [Fact]
    public void CodeTables_ArePrefixFree()
    {
        AssertPrefixFree([.. CcittFax.WhiteTerminating, .. CcittFax.WhiteMakeup, .. CcittFax.ExtendedMakeup, Eol]);
        AssertPrefixFree([.. CcittFax.BlackTerminating, .. CcittFax.BlackMakeup, .. CcittFax.ExtendedMakeup, Eol]);
        Assert.Equal(64, CcittFax.WhiteTerminating.Length);
        Assert.Equal(64, CcittFax.BlackTerminating.Length);
        Assert.Equal(27, CcittFax.WhiteMakeup.Length);
        Assert.Equal(27, CcittFax.BlackMakeup.Length);
        Assert.Equal(13, CcittFax.ExtendedMakeup.Length);

        static void AssertPrefixFree(string[] codes)
        {
            foreach (var a in codes)
            foreach (var b in codes)
                Assert.True(ReferenceEquals(a, b) || !b.StartsWith(a, StringComparison.Ordinal), $"{a} ist Anfang von {b}");
        }
    }

    [Fact]
    public void StencilImage_IsDecodedBeforeItIsPainted()
    {
        // Stencil mask with CCITT (e.g. a logo from a printer driver): without decoding first, the raw data would be read as pixels
        var dict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(8)), ("Height", PdfInteger.Of(2)),
            ("ImageMask", PdfBoolean.True), ("Filter", TestPdf.N("CCITTFaxDecode")),
            ("DecodeParms", TestPdf.Dict(("K", PdfInteger.Of(-1)), ("Columns", PdfInteger.Of(8)))));
        var (doc, list) = InterpreterTests.Page("q 8 0 0 2 0 0 cm /Im Do Q", pdf =>
            TestPdf.Dict(("XObject", TestPdf.Dict(("Im", TestPdf.Ref(pdf.Add(new PdfStream(dict, Pack(Row2DFirst + Row2DSame)))))))));
        using (doc)
        {
            var image = PdfImageDecoder.Decode(list.Images[0], doc);
            Assert.NotNull(image);
            for (var y = 0; y < 2; y++)
            for (var x = 0; x < 8; x++)
                Assert.Equal(x is >= 2 and < 5 ? 255 : 0, image.Rgba[(y * 8 + x) * 4 + 3]);
        }
    }

    /// <summary>
    /// 7.4.6, Table 11: without /DecodeParms K is 0 (one-dimensional, Group 3), Columns 1728, Rows 0 and BlackIs1 false — one white row of 1728
    /// pixels coded one-dimensionally (make-up code 1728, terminating code 0) reads as 216 bytes of white (1-bits), undamaged.
    /// EncodedByteAlign and EndOfBlock: <see cref="Defaults_ByteAlignAndEndOfBlock_AsTheirOpposite"/>. EndOfLine needs no default — the EOL
    /// pattern is always accepted (row EndOfLine); DamagedRowsBeforeError 0 is read on past a damaged row (S-A A72).
    /// </summary>
    [Fact]
    public void Defaults_OfTable11()
    {
        var d = new PdfDictionary();
        d.Set(PdfName.Filter, PdfName.Get("CCITTFaxDecode"));
        var result = StreamFilters.Decode(Pack("010011011" + "00110101"), d);
        Assert.Equal(216, result.Data.Length);
        Assert.All(result.Data, b => Assert.Equal(0xFF, b));
        Assert.False(result.Truncated);
    }

    /// <summary>
    /// 7.4.6, Table 11: the defaults EncodedByteAlign false and EndOfBlock true, each against its opposite — two one-dimensional rows of 10 bits
    /// packed without fill bits read alike without the entry and with false, not with true; EOFB overrides a too small /Rows alike without the
    /// entry and with true, not with false.
    /// </summary>
    [Fact]
    public void Defaults_ByteAlignAndEndOfBlock_AsTheirOpposite()
    {
        var plain = Decode(Row1D + Row1D, ("Rows", PdfInteger.Of(2)));
        Assert.Equal([Row, Row], plain.Data);
        Assert.Equal(plain.Data, Decode(Row1D + Row1D, ("Rows", PdfInteger.Of(2)), ("EncodedByteAlign", PdfBoolean.False)).Data);
        Assert.False(plain.Truncated);
        // with true the second row is looked for at bit 16: it does not decode there — damaged (the row before stands in for it, S-A A72)
        var aligned = Decode(Row1D + Row1D, ("Rows", PdfInteger.Of(2)), ("EncodedByteAlign", PdfBoolean.True));
        Assert.True(aligned.Truncated || !aligned.Data.SequenceEqual(plain.Data));

        var data = Pack(Row2DFirst + Row2DSame + Row2DSame + Eol + Eol);
        PdfDictionary Parms(PdfObject? endOfBlock)
        {
            var d = new PdfDictionary();
            d.Set(PdfName.Filter, PdfName.Get("CCITTFaxDecode"));
            var p = TestPdf.Dict(("K", PdfInteger.Of(-1)), ("Columns", PdfInteger.Of(8)), ("Rows", PdfInteger.Of(1)));
            if (endOfBlock is not null) p.Set(PdfName.Get("EndOfBlock"), endOfBlock);
            d.Set(PdfName.DecodeParms, p);
            return d;
        }
        var none = StreamFilters.Decode(data, Parms(null), sizeHint: 3).Data;
        Assert.Equal(StreamFilters.Decode(data, Parms(PdfBoolean.True), sizeHint: 3).Data, none);
        Assert.NotEqual(StreamFilters.Decode(data, Parms(PdfBoolean.False), sizeHint: 3).Data, none);
    }
}
