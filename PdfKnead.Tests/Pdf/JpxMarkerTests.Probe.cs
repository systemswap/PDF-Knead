using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Findings of the probe of the M2 report (T.800 A.4.2, A.5.1, A.6, A.7.4, A.7.5, B.9; T.801 A.3.6, A.3.9; 23.11.2026), fixed and tested here:
/// marker segments in a place the standard does not allow are read as far as they can be and reported (decided on 23.11.2026: read and report).
/// </summary>
public sealed partial class JpxMarkerTests
{
    /// <summary>A codestream of the rich test image with <paramref name="segment"/> between SOC and SIZ.</summary>
    private static byte[] BeforeSiz(byte[] segment, byte[] cs) => [0xFF, 0x4F, .. segment, .. cs[2..]];

    // ---- A.5.1, A.2: SIZ directly behind SOC

    /// <summary>
    /// T.800 A.5.1, A.2 (Table A.2): SIZ follows SOC directly. A comment, a segment of a code no part defines, or the PPM with all packet headers
    /// between SOC and SIZ: read behind SIZ — the PPM gives its headers —, the image is the source, and the codestream counts as damaged. Before
    /// they went through without a word.
    /// </summary>
    [Theory]
    [InlineData("COM")]
    [InlineData("unknown")]
    [InlineData("PPM")]
    public void SegmentBeforeSiz_ReadBehindIt_Reported(string kind)
    {
        var enc = AnnexA67Encoder.Rich();
        var packets = enc.Packets();
        var headers = AnnexA67Encoder.Headers(packets);
        var cs = kind == "PPM"
            ? BeforeSiz(AnnexA67.Ppm(0, [.. AnnexA67.U32(headers.Length), .. headers]),
                AnnexA67.Stream(enc.Siz(), enc.MainHeader(), (0, [], AnnexA67Encoder.Bodies(packets))))
            : BeforeSiz(kind == "COM" ? AnnexA67.Seg(0xFF64, 0, 1, 0x70, 0x6B) : AnnexA67.Seg(0xFF6C, 1, 2, 3), enc.Build(packets));
        var (planes, truncated) = AnnexA67.Read(cs);
        Assert.Equal(enc.Samples, planes);
        Assert.True(truncated);
        Assert.False(AnnexA67.Read(enc.Build(packets)).Truncated);
    }

    /// <summary>
    /// T.800 A.5.1 and A.5.2: a CAP between SOC and SIZ is evaluated behind SIZ, where Rsiz is known. With bit 14 of Rsiz set, a code-block style
    /// with one of the bits a decoder may otherwise ignore (Table A.19: 0x40) is refused, as with CAP behind SIZ (No. 967); without such bits
    /// the image is the source, reported for the place of CAP. Before CAP before SIZ was dropped (Rsiz still 0), and the bits went through.
    /// </summary>
    [Fact]
    public void CapBeforeSiz_EvaluatedBehindIt()
    {
        var enc = AnnexA67Encoder.Rich();
        var plain = enc.Build(enc.Packets());
        plain[6] = 0x40;   // Rsiz 0x4000: CAP present (A.5.2)
        var cap = AnnexA67.Seg(0xFF50, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00);
        var before = BeforeSiz(cap, plain);
        var (planes, truncated) = AnnexA67.Read(before);
        Assert.Equal(enc.Samples, planes);
        Assert.True(truncated);

        var styled = AnnexA67.Patch(before, 0xFF52, 8, 0x40);
        Assert.Contains("CAP", Assert.Throws<JpxException>(() => JpxDecoder.DecodePlanes(styled, long.MaxValue)).Message);
        // the same CAP behind SIZ (where it belongs): refused alike; without CAP the bit is ignored
        int sizEnd = 4 + AnnexA67.Length(plain[2..]);
        byte[] behind = [.. plain[..sizEnd], .. cap, .. plain[sizEnd..]];
        Assert.Contains("CAP", Assert.Throws<JpxException>(() => JpxDecoder.DecodePlanes(AnnexA67.Patch(behind, 0xFF52, 8, 0x40), long.MaxValue)).Message);
        Assert.Equal(enc.Samples, AnnexA67.Read(AnnexA67.Patch(plain, 0xFF52, 8, 0x40)).Planes);
    }

    /// <summary>
    /// T.800 A.5.1: one SIZ per codestream. A second one in the main header (another size, one component) is passed over, the first holds — the
    /// image is the source —, and reported. Before it replaced size, tiling and components without a word.
    /// </summary>
    [Fact]
    public void SecondSiz_PassedOver_Reported()
    {
        var enc = AnnexA67Encoder.Rich();
        var packets = enc.Packets();
        var cs = AnnexA67.Stream(enc.Siz(), [.. enc.MainHeader(), AnnexA67.Siz(8, 8, 1)], (0, [], AnnexA67Encoder.InStream(packets)));
        var (planes, truncated) = AnnexA67.Read(cs);
        Assert.Equal(enc.Samples, planes);
        Assert.True(truncated);
    }

    // ---- A.6.1 to A.6.5: once per header (per component), only in the first tile-part

    /// <summary>The segment <paramref name="marker"/> for component 0 with the values of the rich test image (its COD, QCD; COC and QCC
    /// copied from them; RGN without shift); <paramref name="altered"/>: a value changed that would change the image if it were applied.</summary>
    private static byte[] SegmentOf(AnnexA67Encoder enc, int marker, bool altered = false)
    {
        var cod = enc.Cod();
        var qcd = enc.Qcd();
        byte[] spcod = [.. cod[9..]];
        if (altered) spcod[4] = 0;                       // the 9-7 wavelet instead of 5-3
        byte[] sqcd = [.. qcd[4..]];
        if (altered) sqcd[0] += 0x20;                    // one guard bit more: Mb one larger (E-2)
        return marker switch
        {
            0xFF52 => AnnexA67.Seg(0xFF52, [.. cod[4..9], .. spcod]),
            0xFF53 => AnnexA67.Seg(0xFF53, [0, (byte)(cod[4] & 1), .. spcod]),
            0xFF5C => AnnexA67.Seg(0xFF5C, sqcd),
            0xFF5D => AnnexA67.Seg(0xFF5D, [0, .. sqcd]),
            _ => AnnexA67.Rgn(2, 0, 0, altered ? 5 : 0),
        };
    }

    /// <summary>
    /// T.800 A.6.1 to A.6.5: COD and QCD once per header, COC, QCC and RGN at most once per component. The same segment twice in the main
    /// header or in the tile-part header is read as before (the last holds; here they are alike, so the image is the source) and reported;
    /// once it is not reported. Before a second one went without a word.
    /// </summary>
    [Theory]
    [InlineData(0xFF52, "main")]
    [InlineData(0xFF53, "main")]
    [InlineData(0xFF5C, "main")]
    [InlineData(0xFF5D, "main")]
    [InlineData(0xFF5E, "main")]
    [InlineData(0xFF52, "tile")]
    [InlineData(0xFF53, "tile")]
    [InlineData(0xFF5C, "tile")]
    [InlineData(0xFF5D, "tile")]
    [InlineData(0xFF5E, "tile")]
    public void SegmentTwiceInOneHeader_ReadAsBefore_Reported(int marker, string where)
    {
        var enc = AnnexA67Encoder.Rich();
        var packets = enc.Packets();
        var segment = SegmentOf(enc, marker);
        // COD and QCD stand in the main header already: there one more makes two
        var inMain = marker is 0xFF52 or 0xFF5C ? 1 : 0;
        byte[] Build(int count) => where == "main"
            ? AnnexA67.Stream(enc.Siz(), [.. enc.MainHeader(), .. Enumerable.Repeat(segment, count - inMain)], (0, [], AnnexA67Encoder.InStream(packets)))
            : AnnexA67.Stream(enc.Siz(), enc.MainHeader(), (0, [.. Enumerable.Repeat(segment, count)], AnnexA67Encoder.InStream(packets)));
        var once = AnnexA67.Read(Build(1));
        Assert.Equal(enc.Samples, once.Planes);
        Assert.False(once.Truncated);
        var twice = AnnexA67.Read(Build(2));
        Assert.Equal(enc.Samples, twice.Planes);
        Assert.True(twice.Truncated);
    }

    /// <summary>
    /// T.800 A.4.2, A.6.1 to A.6.5: COD, COC, QCD, QCC and RGN of a tile only in its first tile-part. One in the second tile-part — with a value
    /// that would change the image — is passed over as before (the image is the source) and now reported; in the first tile-part the same
    /// segment applies (the image changes) and is not reported. RGN is the exception to the last check: with the maximum shift method (H.1) a
    /// shift the encoder did not use leaves every coefficient as it is — each nonzero one lies above the shift and is scaled back.
    /// </summary>
    [Theory]
    [InlineData(0xFF52)]
    [InlineData(0xFF53)]
    [InlineData(0xFF5C)]
    [InlineData(0xFF5D)]
    [InlineData(0xFF5E)]
    public void SegmentInALaterTilePart_PassedOver_Reported(int marker)
    {
        var enc = AnnexA67Encoder.Rich();
        var packets = enc.Packets();
        var altered = SegmentOf(enc, marker, altered: true);
        List<AnnexA67Encoder.Packet> first = [.. packets.Take(16)], second = [.. packets.Skip(16)];
        byte[] Build(byte[][] firstHeader, byte[][] secondHeader) => AnnexA67.Stream(enc.Siz(), enc.MainHeader(),
            (0, firstHeader, AnnexA67Encoder.InStream(first)), (0, secondHeader, AnnexA67Encoder.InStream(second)));
        var plain = AnnexA67.Read(Build([], []));
        Assert.Equal(enc.Samples, plain.Planes);
        Assert.False(plain.Truncated);
        var later = AnnexA67.Read(Build([], [altered]));
        Assert.Equal(enc.Samples, later.Planes);
        Assert.True(later.Truncated);
        if (marker == 0xFF5E) return;
        try
        {
            var applied = AnnexA67.Read(Build([altered], []));
            Assert.NotEqual(enc.Samples, applied.Planes);
        }
        catch (JpxException)
        {
            // applied, the altered value may make the packets unreadable: that too shows it is not passed over in the first tile-part
        }
    }

    // ---- A.7.4, A.7.5: PPM and PPT

    /// <summary>
    /// T.800 A.7.5: with a PPM in the main header no PPT. A PPT besides (its headers reversed, so they could not stand for the packets) is passed
    /// over — the PPM holds, the image is the source — and reported; the PPM alone is not. A PPT in the main header and a PPM in a tile-part
    /// header (A.7.4: main header only) are passed over likewise and reported.
    /// </summary>
    [Fact]
    public void Ppm_BesidePptOrInTheWrongHeader_Reported()
    {
        var enc = AnnexA67Encoder.Rich();
        var packets = enc.Packets();
        var headers = AnnexA67Encoder.Headers(packets);
        var bodies = AnnexA67Encoder.Bodies(packets);
        byte[] ippm = [.. AnnexA67.U32(headers.Length), .. headers];
        byte[][] main = [.. enc.MainHeader(), AnnexA67.Ppm(0, ippm)];
        var ppmAlone = AnnexA67.Read(AnnexA67.Stream(enc.Siz(), main, (0, [], bodies)));
        Assert.Equal(enc.Samples, ppmAlone.Planes);
        Assert.False(ppmAlone.Truncated);
        var both = AnnexA67.Read(AnnexA67.Stream(enc.Siz(), main, (0, [AnnexA67.Ppt(0, [.. Enumerable.Reverse(headers)])], bodies)));
        Assert.Equal(enc.Samples, both.Planes);
        Assert.True(both.Truncated);
        var pptInMain = AnnexA67.Read(AnnexA67.Stream(enc.Siz(), [.. enc.MainHeader(), AnnexA67.Ppt(0, headers)], (0, [], AnnexA67Encoder.InStream(packets))));
        Assert.Equal(enc.Samples, pptInMain.Planes);
        Assert.True(pptInMain.Truncated);
        var ppmInPart = AnnexA67.Read(AnnexA67.Stream(enc.Siz(), enc.MainHeader(), (0, [AnnexA67.Ppm(0, ippm)], AnnexA67Encoder.InStream(packets))));
        Assert.Equal(enc.Samples, ppmInPart.Planes);
        Assert.True(ppmInPart.Truncated);
    }

    // ---- T.801 A.3.9: MCO

    /// <summary>
    /// T.801 A.3.9, Equation A-6: Lmco = 3 + Nmco. In a codestream with the multiple component capability (Rsiz 0x8100) an MCO of Nmco 0 is
    /// read silently; one with Nmco 1 but no index (Lmco 3), one without Nmco (Lmco 2) and one with a byte beyond its Nmco 0 indices give the
    /// stages they have (none) and the codestream counts as damaged, as with NLT. A second MCO in the same header (at most one) is read as
    /// before (the last holds) and reported. Before a short MCO went without a word.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0 }, 1, false)]
    [InlineData(new byte[] { 1 }, 1, true)]
    [InlineData(new byte[0], 1, true)]
    [InlineData(new byte[] { 0, 7 }, 1, true)]
    [InlineData(new byte[] { 0 }, 2, true)]
    public void Mco_LengthNotAsEquationA6_Reported(byte[] body, int count, bool reported)
    {
        var enc = AnnexA67Encoder.Rich();
        var mco = AnnexA67.Seg(0xFF77, body);
        var cs = enc.Build(enc.Packets(), main: [.. Enumerable.Repeat(mco, count)]);
        cs[6] = 0x81;   // Rsiz 0x8100: ISO/IEC 15444-2, the multiple component transformation
        var (planes, truncated) = AnnexA67.Read(cs);
        Assert.Equal(enc.Samples, planes);
        Assert.Equal(reported, truncated);
    }

    // ---- B.9, Table B.3: coding passes of code-blocks in packets

    /// <summary>
    /// T.800 B.9 (Table B.3, values of this test) and B.8: three code-blocks of one precinct with 4, 6 and 9 missing most significant bit-planes
    /// (coefficients up to ±100, ±20 and ±3 of 11 bit-planes), their coding passes put into four layers by bit-plane — planes 0–2, 3–5, 6–8,
    /// 9–10 —, so that layer 0 is an empty packet (B.9: it must still appear), the first block comes in with layer 1, the second with layer 2,
    /// the third with layer 3 only. The decoder gives the coefficients back.
    /// </summary>
    [Fact]
    public void CodingPassesByBitPlane_IntoLayers_AsTableB3()
    {
        const int layers = 4, planes = 11;
        var magnitude = new[] { 100, 20, 3 };
        var coefficients = new int[12 * 4];
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 12; x++)
        {
            var m = magnitude[x / 4];
            coefficients[y * 12 + x] = (x * 7 + y * 13) % (2 * m + 1) - m;
        }
        var enc = new AnnexA67Encoder
        {
            Width = 12, Height = 4, Samples = [coefficients], Direct = true, Levels = 0, Layers = layers, Xcb = 2, Ycb = 2,
            Split = (zero, passes) =>
            {
                // pass p lies in bit-plane zero + (p + 2) / 3 (a cleanup pass first, then significance, refinement, cleanup per plane)
                int LayerOf(int p) => Math.Min(layers - 1, (zero + (p + 2) / 3) * layers / planes);
                return [.. Enumerable.Range(0, layers + 1).Select(l => Enumerable.Range(0, passes).Count(p => LayerOf(p) < l))];
            },
        };
        var packets = enc.Packets();
        Assert.Equal(layers, packets.Count);
        Assert.Empty(packets[0].Data);
        Assert.All(packets.Skip(1), p => Assert.NotEmpty(p.Data));
        Assert.Equal([[.. coefficients.Select(v => v + 128)]], AnnexA67.Decoded(enc.Build(packets)));
    }

    // ---- T.801 A.3.7, A.3.8: MCT and MCC too short

    /// <summary>
    /// T.801 A.3.7 and A.3.8: an MCT carries Zmct and Imct (and Ymct in the first segment of a series), an MCC Zmcc and Imcc (and Ymcc, Qmcc in
    /// the first). In a codestream with the multiple component capability (Rsiz 0x8100) a segment too short for these fields is passed over —
    /// the image the same — and the codestream counts as damaged, as with MCO and NLT; a complete first MCT and MCC without a stage that uses
    /// them stay silent. Before a short MCT or MCC went without a word (probe of 23.11.2026).
    /// </summary>
    [Theory]
    [InlineData(0xFF74, new byte[] { 0, 0 }, true)]
    [InlineData(0xFF74, new byte[] { 0, 0, 0x01, 0x00 }, true)]
    [InlineData(0xFF74, new byte[] { 0, 0, 0x01, 0x00, 0, 0 }, false)]
    [InlineData(0xFF75, new byte[] { 0, 0 }, true)]
    [InlineData(0xFF75, new byte[] { 0, 0, 1, 0, 0 }, true)]
    [InlineData(0xFF75, new byte[] { 0, 0, 1, 0, 0, 0, 0 }, false)]
    public void MctMcc_TooShort_PassedOver_Reported(int marker, byte[] body, bool reported)
    {
        var enc = AnnexA67Encoder.Rich();
        var cs = enc.Build(enc.Packets(), main: [AnnexA67.Seg(marker, body)]);
        cs[6] = 0x81;   // Rsiz 0x8100: ISO/IEC 15444-2, the multiple component transformation
        var (planes, truncated) = AnnexA67.Read(cs);
        Assert.Equal(enc.Samples, planes);
        Assert.Equal(reported, truncated);
    }
}
