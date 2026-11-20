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
}
