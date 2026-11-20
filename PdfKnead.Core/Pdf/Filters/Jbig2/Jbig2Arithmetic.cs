namespace PdfKnead.Core.Pdf.Filters.Jbig2;

/// <summary>
/// The MQ arithmetic decoder of JBIG2 and JPEG 2000, written from the flow charts of the standard: ITU-T T.800 | ISO/IEC 15444-1, Annex C.3
/// (Figures C.14 to C.20) — the same decoder as ITU-T T.88 | ISO/IEC 14492, Annex E.3. No code of another decoder was used (the decoder taken
/// over from another project before was replaced by this one on 16.11.2026; docs/LIZENZPRUEFUNG.md).
/// <para>
/// The registers as in Table C.3: the code register C is one 32-bit register — its high half is Chigh, which alone is compared with Qe, its
/// low half Clow, whose upper byte takes the new data; A is the interval, CT counts the bits left in Clow. Each context is one byte,
/// (index &lt;&lt; 1) | MPS. Beyond the end the data read as 0xFF, so that BYTEIN feeds 1-bits as at a marker (C.3.4): data cut off without
/// their closing marker still decode.
/// </para>
/// </summary>
internal sealed class MqDecoder
{
    /// <summary>Qe, the estimate of the LPS probability for each index (Table C.2; Table E.1 of T.88).</summary>
    internal static readonly ushort[] Qe =
    [
        0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401,
        0x1C01, 0x1601, 0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201,
        0x1C01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101, 0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1,
        0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025, 0x0015, 0x0009, 0x0005, 0x0001, 0x5601,
    ];

    /// <summary>NMPS, the next index after an MPS (Table C.2).</summary>
    internal static readonly byte[] Nmps =
    [
        1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24,
        25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46,
    ];

    /// <summary>NLPS, the next index after an LPS (Table C.2).</summary>
    internal static readonly byte[] Nlps =
    [
        1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21,
        22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46,
    ];

    /// <summary>SWITCH, whether an LPS turns the sense of the MPS (Table C.2: indexes 0, 6 and 14).</summary>
    internal static readonly bool[] Switch = [true, false, false, false, false, false, true, false, false, false, false, false,
        false, false, true, false, false, false, false, false, false, false, false, false, false, false, false, false, false,
        false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false];

    private readonly byte[] _data;
    private readonly int _end;

    /// <summary>BP, the pointer to the current byte of the compressed data.</summary>
    private int _bp;

    /// <summary>C, the code register: Chigh in bits 16 to 31, Clow in bits 0 to 15.</summary>
    private uint _c;

    /// <summary>A, the interval register.</summary>
    private uint _a;

    /// <summary>CT, the bits left in Clow before the next byte is needed.</summary>
    private int _ct;

    public MqDecoder(byte[] data, int start, int end)
    {
        _data = data;
        _end = end;
        InitDec(start);
    }

    /// <summary>The byte at <paramref name="i"/>; 0xFF beyond the end of the data.</summary>
    private int B(int i) => i < _end ? _data[i] : 0xFF;

    /// <summary>
    /// INITDEC (Figure C.20): BP at the first byte; that byte into the low byte of Chigh, the next one read in; C shifted by 7 bits and CT
    /// lowered by 7, which aligns C with A = 0x8000.
    /// </summary>
    private void InitDec(int start)
    {
        _bp = start;
        _c = (uint)B(_bp) << 16;
        ByteIn();
        _c <<= 7;
        _ct -= 7;
        _a = 0x8000;
    }

    /// <summary>
    /// BYTEIN (Figure C.19): an ordinary byte goes into the upper byte of Clow (CT = 8). After 0xFF the next byte decides: above 0x8F it is a
    /// marker — BP stays on its 0xFF and 1-bits are fed (0xFF00, CT = 8); otherwise it carries a stuff bit and goes in one bit higher, so that
    /// the stuff bit (with any carry) meets the lowest bit of Chigh (CT = 7).
    /// </summary>
    private void ByteIn()
    {
        if (B(_bp) != 0xFF)
        {
            _bp++;
            _c += (uint)B(_bp) << 8;
            _ct = 8;
        }
        else if (B(_bp + 1) > 0x8F)
        {
            _c += 0xFF00;
            _ct = 8;
        }
        else
        {
            _bp++;
            _c += (uint)B(_bp) << 9;
            _ct = 7;
        }
    }

    /// <summary>
    /// DECODE (Figure C.15): one decision in the context <paramref name="cx"/>[<paramref name="index"/>]. A shrinks by Qe; Chigh below Qe
    /// is the LPS sub-interval (with the conditional exchange of Figure C.17), otherwise Chigh is lowered by Qe — and while A keeps its top
    /// bit the MPS is decoded at once; else the conditional exchange of Figure C.16 decides. Each exchange is followed by RENORMD.
    /// </summary>
    public int Decode(byte[] cx, int index)
    {
        var state = cx[index];
        int i = state >> 1, mps = state & 1, d;
        uint qe = Qe[i];
        _a -= qe;
        if (_c >> 16 < qe)
        {
            // LPS_EXCHANGE (Figure C.17): A becomes Qe on both paths
            if (_a < qe)
            {
                d = mps;
                i = Nmps[i];
            }
            else
            {
                d = 1 - mps;
                if (Switch[i]) mps = 1 - mps;
                i = Nlps[i];
            }
            _a = qe;
        }
        else
        {
            _c -= qe << 16;
            if ((_a & 0x8000) != 0) return mps;
            // MPS_EXCHANGE (Figure C.16)
            if (_a < qe)
            {
                d = 1 - mps;
                if (Switch[i]) mps = 1 - mps;
                i = Nlps[i];
            }
            else
            {
                d = mps;
                i = Nmps[i];
            }
        }
        cx[index] = (byte)((i << 1) | mps);
        RenormD();
        return d;
    }

    /// <summary>
    /// RENORMD (Figure C.18): A and C doubled bit by bit until the top bit of A is set; a new byte comes in through BYTEIN whenever CT has
    /// run out. Chigh stays below A, so C keeps within 32 bits.
    /// </summary>
    private void RenormD()
    {
        do
        {
            if (_ct == 0) ByteIn();
            _a <<= 1;
            _c <<= 1;
            _ct--;
        } while ((_a & 0x8000) == 0);
    }

    /// <summary>Decodes an integer (ITU-T T.88, A.2, procedures IADH, IADW, …); null = OOB.</summary>
    public int? DecodeInt(byte[] cx)
    {
        var prev = 1;
        int Bits(int n)
        {
            var v = 0L;
            for (var k = 0; k < n; k++)
            {
                var bit = Decode(cx, prev);
                prev = prev < 256 ? (prev << 1) | bit : (((prev << 1) | bit) & 511) | 256;
                v = (v << 1) | (uint)bit;
            }
            return (int)Math.Min(v, int.MaxValue / 2);
        }

        var sign = Bits(1);
        var value = Bits(1) == 0 ? Bits(2)
            : Bits(1) == 0 ? Bits(4) + 4
            : Bits(1) == 0 ? Bits(6) + 20
            : Bits(1) == 0 ? Bits(8) + 84
            : Bits(1) == 0 ? Bits(12) + 340
            : Bits(32) + 4436;
        if (sign == 0) return value;
        return value > 0 ? -value : null;
    }

    /// <summary>Like <see cref="DecodeInt"/>, but OOB is an error here.</summary>
    public int Int(byte[] cx) => DecodeInt(cx) ?? throw new Jbig2Exception("JBIG2: unerwartetes OOB");

    /// <summary>Decodes a symbol ID (ITU-T T.88, A.3, IAID) with <paramref name="codeLength"/> bits.</summary>
    public int DecodeId(byte[] cx, int codeLength)
    {
        var prev = 1;
        for (var k = 0; k < codeLength; k++) prev = (prev << 1) | Decode(cx, prev);
        return prev - (1 << codeLength);
    }
}

/// <summary>The contexts of one segment (all procedures share one decoder).</summary>
internal sealed class Jbig2Contexts
{
    public byte[] Gb = new byte[1 << 16];
    public byte[] Gr = new byte[1 << 13];
    public readonly byte[] Iadh = new byte[512], Iadw = new byte[512], Iaex = new byte[512], Iaai = new byte[512];
    public readonly byte[] Iadt = new byte[512], Iafs = new byte[512], Iads = new byte[512], Iait = new byte[512];
    public readonly byte[] Iari = new byte[512], Iardw = new byte[512], Iardh = new byte[512], Iardx = new byte[512], Iardy = new byte[512];
    private byte[]? _iaid;

    public byte[] Iaid(int codeLength)
    {
        if (codeLength > 24) throw new Jbig2Exception("JBIG2: zu viele Symbole");
        if (_iaid is null || _iaid.Length != 1 << (codeLength + 1)) _iaid = new byte[1 << (codeLength + 1)];
        return _iaid;
    }
}
