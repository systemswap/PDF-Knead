namespace PdfKnead.Core.Pdf.Filters.Jpeg;

/// <summary>
/// Arithmetic decoding (ITU-T T.81, Annex D: the binary decoder; F.2.4: sequential; G.1.3: progressive). Statistics belong to the table
/// numbers the scan names (one area per DC and AC table), the DC context and prediction to each component of the scan. Damaged data
/// (a run past the end of the band, a magnitude beyond 15 bits) ends the restart interval with zeros and is reported.
/// </summary>
internal sealed class JpegArithmetic : JpegEntropy
{
    // Table D.3: Qe, next index after an LPS and after an MPS, and whether the MPS sense switches after an LPS
    private static readonly ushort[] Qe =
    [
        0x5A1D, 0x2586, 0x1114, 0x080B, 0x03D8, 0x01DA, 0x00E5, 0x006F, 0x0036, 0x001A, 0x000D, 0x0006, 0x0003, 0x0001, 0x5A7F, 0x3F25,
        0x2CF2, 0x207C, 0x17B9, 0x1182, 0x0CEF, 0x09A1, 0x072F, 0x055C, 0x0406, 0x0303, 0x0240, 0x01B1, 0x0144, 0x00F5, 0x00B7, 0x008A,
        0x0068, 0x004E, 0x003B, 0x002C, 0x5AE1, 0x484C, 0x3A0D, 0x2EF1, 0x261F, 0x1F33, 0x19A8, 0x1518, 0x1177, 0x0E74, 0x0BFB, 0x09F8,
        0x0861, 0x0706, 0x05CD, 0x04DE, 0x040F, 0x0363, 0x02D4, 0x025C, 0x01F8, 0x01A4, 0x0160, 0x0125, 0x00F6, 0x00CB, 0x00AB, 0x008F,
        0x5B12, 0x4D04, 0x412C, 0x37D8, 0x2FE8, 0x293C, 0x2379, 0x1EDF, 0x1AA9, 0x174E, 0x1424, 0x119C, 0x0F6B, 0x0D51, 0x0BB6, 0x0A40,
        0x5832, 0x4D1C, 0x438E, 0x3BDD, 0x34EE, 0x2EAE, 0x299A, 0x2516, 0x5570, 0x4CA9, 0x44D9, 0x3E22, 0x3824, 0x32B4, 0x2E17, 0x56A8,
        0x4F46, 0x47E5, 0x41CF, 0x3C3D, 0x375E, 0x5231, 0x4C0F, 0x4639, 0x415E, 0x5627, 0x50E7, 0x4B85, 0x5597, 0x504F, 0x5A10, 0x5522,
        0x59EB,
    ];

    private static readonly byte[] NextLps =
    [
        1, 14, 16, 18, 20, 23, 25, 28, 30, 33, 35, 9, 10, 12, 15, 36, 38, 39, 40, 42, 43, 45, 46, 48, 49, 51, 52, 54, 56, 57, 59, 60,
        62, 63, 32, 33, 37, 64, 65, 67, 68, 69, 70, 72, 73, 74, 75, 77, 78, 79, 48, 50, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 61, 61,
        65, 80, 81, 82, 83, 84, 86, 87, 87, 72, 72, 74, 74, 75, 77, 77, 80, 88, 89, 90, 91, 92, 93, 86, 88, 95, 96, 97, 99, 99, 93, 95,
        101, 102, 103, 104, 99, 105, 106, 107, 103, 105, 108, 109, 110, 111, 110, 112, 112,
    ];

    private static readonly byte[] NextMps =
    [
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 13, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        33, 34, 35, 9, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 32,
        65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 48, 81, 82, 83, 84, 85, 86, 87, 71, 89, 90, 91, 92, 93, 94, 86, 96,
        97, 98, 99, 100, 93, 102, 103, 104, 99, 106, 107, 103, 109, 107, 111, 109, 111,
    ];

    private static readonly int[] Switches = [0, 14, 36, 64, 80, 88, 95, 105, 110, 112];

    private readonly byte[] _data;
    private readonly int _end;
    private int _position, _marker = -1, _zeros;
    private long _c;
    private int _a, _ct;

    // statistics: Table D.3 index in the low 7 bits, the MPS sense in bit 7 — 64 bins per DC area (49 used), 256 per AC area (245 used)
    private readonly byte[][] _dcStats = [new byte[64], new byte[64], new byte[64], new byte[64]];
    private readonly byte[][] _acStats = [new byte[256], new byte[256], new byte[256], new byte[256]];
    private readonly int[] _dcContext = new int[4], _prediction = new int[4];
    private readonly int[] _dcTable, _acTable;
    private readonly byte[] _l, _u, _kx;
    private bool _broken;

    /// <param name="dcTable">Per component of the scan its DC table number.</param>
    /// <param name="acTable">Per component its AC table number.</param>
    /// <param name="l">Per DC table number the lower conditioning bound L (DAC; default 0).</param>
    /// <param name="u">Per DC table number the upper bound U (default 1).</param>
    /// <param name="kx">Per AC table number Kx (default 5).</param>
    public JpegArithmetic(byte[] data, int position, int end, int[] dcTable, int[] acTable, byte[] l, byte[] u, byte[] kx)
    {
        (_data, _position, _end, _dcTable, _acTable, _l, _u, _kx) = (data, position, end, dcTable, acTable, l, u, kx);
    }

    /// <summary>
    /// <paramref name="count"/> decisions of one statistics bin from its initial state (index 0, MPS 0) — the binary decoder alone, as the
    /// test sequence of ITU-T T.81, K.4.1 uses it (JpegArithmeticTests, local).
    /// </summary>
    internal static byte[] DecisionsOfOneBin(byte[] data, int count)
    {
        var decoder = new JpegArithmetic(data, 0, data.Length, [0], [0], [0], [1], [5]);
        decoder.Reset();
        var stats = new byte[1];
        var decisions = new byte[count];
        for (var i = 0; i < count; i++) decisions[i] = (byte)decoder.Decode(stats, 0);
        return decisions;
    }

    public override void Reset()
    {
        foreach (var s in _dcStats) Array.Clear(s);
        foreach (var s in _acStats) Array.Clear(s);
        Array.Clear(_dcContext);
        Array.Clear(_prediction);
        _broken = false;
        // Initdec (Figure D.22)
        _a = 0x10000;
        _c = 0;
        ByteIn();
        _c <<= 8;
        ByteIn();
        _c <<= 8;
        _ct = 0;
    }

    public override bool Restart(int number)
    {
        if (_marker < 0)
        {
            var at = JpegBits.NextMarker(_data, _position, _end);
            if (at < 0)
            {
                Damaged = true;
                return false;
            }
            if (at != _position) Damaged = true;
            _marker = at;
        }
        var code = _data[_marker + 1];
        if (code is < 0xD0 or > 0xD7) return false;
        if (code != 0xD0 + number) Damaged = true;
        _position = _marker + 2;
        _marker = -1;
        _zeros = 0;
        Reset();
        return true;
    }

    public override int End()
    {
        // a few zero bytes after the end are normal (the decoder reads ahead); many mean the data was cut off
        if (_zeros > 8) Damaged = true;
        return _marker >= 0 ? _marker : JpegBits.NextMarker(_data, _position, _end) is var at and >= 0 ? at : _end;
    }

    /// <summary>Byte_in with Unstuff_0 (Figures D.20, D.21): at a marker zeros follow.</summary>
    private void ByteIn()
    {
        if (_marker < 0 && _position < _end)
        {
            var b = _data[_position];
            if (b != 0xFF)
            {
                _position++;
                _c += (long)b << 8;
                return;
            }
            var p = _position + 1;
            while (p < _end && _data[p] == 0xFF) p++;
            if (p < _end && _data[p] == 0x00)
            {
                _position = p + 1;
                _c |= 0xFF00;
                return;
            }
            if (p < _end) _marker = p - 1;
            _position = p < _end ? p - 1 : _end;
        }
        _zeros++;
    }

    /// <summary>Decode(S) (Figure D.16) for the bin <paramref name="s"/> of <paramref name="stats"/>.</summary>
    private int Decode(byte[] stats, int s)
    {
        var st = stats[s];
        int index = st & 0x7F, mps = st >> 7;
        int qe = Qe[index], d;
        _a -= qe;
        if (_c >> 16 < _a)
        {
            if (_a >= 0x8000) return mps;
            // Cond_MPS_exchange (Figure D.18)
            d = _a < qe ? 1 - mps : mps;
        }
        else
        {
            // Cond_LPS_exchange (Figure D.17)
            _c -= (long)_a << 16;
            d = _a < qe ? mps : 1 - mps;
            _a = qe;
        }
        if (d == mps)
        {
            index = NextMps[index];
        }
        else
        {
            if (Array.BinarySearch(Switches, index) >= 0) mps = 1 - mps;
            index = NextLps[index];
        }
        stats[s] = (byte)(index | mps << 7);
        Renormalize();
        return d;
    }

    /// <summary>A decision with the fixed estimate Qe = 0x5A1D, MPS = 0 (signs of AC coefficients, correction bits of DC).</summary>
    private int DecodeFixed()
    {
        const int qe = 0x5A1D;
        int d;
        _a -= qe;
        if (_c >> 16 < _a)
        {
            if (_a >= 0x8000) return 0;
            d = _a < qe ? 1 : 0;
        }
        else
        {
            _c -= (long)_a << 16;
            d = _a < qe ? 0 : 1;
            _a = qe;
        }
        Renormalize();
        return d;
    }

    /// <summary>Renorm_d (Figure D.19).</summary>
    private void Renormalize()
    {
        do
        {
            if (_ct == 0)
            {
                ByteIn();
                _ct = 8;
            }
            _a <<= 1;
            _c = (_c << 1) & 0xFFFFFFFF;
            _ct--;
        }
        while (_a < 0x8000);
    }

    private void Broken()
    {
        _broken = true;
        Damaged = true;
    }

    /// <summary>
    /// Whether decoding stops until the next restart marker: after damaged data, or when far more zero bytes than the decoder reads ahead
    /// were fed after the end (the data was cut off) — the blocks after that keep zero coefficients instead of values decoded from zeros.
    /// </summary>
    public override bool Dead => Stopped();

    private bool Stopped()
    {
        if (!_broken && _zeros > 8) Broken();
        return _broken;
    }

    /// <summary>
    /// The magnitude after the sign (Figures F.23 and F.24): the first decision at <paramref name="s"/>, the second at
    /// <paramref name="x1"/>, the further ones from <paramref name="x2"/> on, the bits 14 bins further. −1 if beyond 15 bits.
    /// </summary>
    private int Magnitude(byte[] stats, int s, int x1, int x2)
    {
        if (Decode(stats, s) == 0) return 0;
        var m = 1;
        if (Decode(stats, x1) != 0)
        {
            m = 2;
            s = x2;
            while (Decode(stats, s) != 0)
            {
                m <<= 1;
                if (m == 0x8000) return -1;
                s++;
            }
        }
        else
        {
            s = x1;
        }
        var v = m;
        s += 14;
        for (var bit = m >> 1; bit != 0; bit >>= 1)
            if (Decode(stats, s) != 0) v |= bit;
        return v;
    }

    /// <summary>A DC difference (Figure F.19) with its context for the next block (F.1.4.4.1.2).</summary>
    private int DcDiff(int slot)
    {
        var t = _dcTable[slot];
        var stats = _dcStats[t];
        var s0 = _dcContext[slot];
        if (Decode(stats, s0) == 0)
        {
            _dcContext[slot] = 0;
            return 0;
        }
        var sign = Decode(stats, s0 + 1);
        var sz = Magnitude(stats, s0 + 2 + sign, 20, 21);
        if (sz < 0)
        {
            Broken();
            return 0;
        }
        var v = sz + 1;
        // zero, small or large by the bounds of the DAC segment: small if 2^(L−1) < |v| ≤ 2^U (L = 0: above 0)
        var lower = _l[t] == 0 ? 0 : 1 << (_l[t] - 1);
        _dcContext[slot] = v <= lower ? 0 : v > 1 << _u[t] ? 12 + 4 * sign : 4 + 4 * sign;
        return sign != 0 ? -v : v;
    }

    /// <summary>A non-zero AC coefficient at zig-zag index <paramref name="k"/> (F.2.4.3); null if damaged.</summary>
    private int? AcValue(byte[] stats, int t, int k)
    {
        var s0 = 3 * (k - 1) + 1;
        var sign = DecodeFixed();
        var sz = Magnitude(stats, s0 + 1, s0 + 1, k <= _kx[t] ? 189 : 217);
        if (sz < 0) return null;
        return sign != 0 ? -(sz + 1) : sz + 1;
    }

    public override void Sequential(int slot, Span<short> block)
    {
        if (Stopped()) return;
        _prediction[slot] += DcDiff(slot);
        block[0] = (short)Math.Clamp(_prediction[slot], short.MinValue, short.MaxValue);
        if (_broken) return;
        AcBand(slot, block, 1, 63, 0);
    }

    public override void DcFirst(int slot, Span<short> block, int al)
    {
        if (Stopped()) return;
        _prediction[slot] += DcDiff(slot);
        block[0] = Shifted(_prediction[slot], al);
    }

    public override void DcRefine(Span<short> block, int al)
    {
        if (Stopped()) return;
        if (DecodeFixed() != 0) block[0] = (short)((ushort)block[0] | 1 << al);
    }

    public override void AcFirst(int slot, Span<short> block, int ss, int se, int al)
    {
        if (Stopped()) return;
        AcBand(slot, block, ss, se, al);
    }

    /// <summary>The coefficients <paramref name="ss"/>–<paramref name="se"/> (Figure F.20; G.1.3.2 with the point transform).</summary>
    private void AcBand(int slot, Span<short> block, int ss, int se, int al)
    {
        var t = _acTable[slot];
        var stats = _acStats[t];
        for (var k = ss; k <= se; k++)
        {
            if (Decode(stats, 3 * (k - 1)) != 0) return;   // end of band
            while (Decode(stats, 3 * (k - 1) + 1) == 0)
            {
                if (++k > se)
                {
                    Broken();
                    return;
                }
            }
            if (AcValue(stats, t, k) is not { } v)
            {
                Broken();
                return;
            }
            block[ZigZag[k]] = Shifted(v, al);
        }
    }

    public override void AcRefine(int slot, Span<short> block, int ss, int se, int al)
    {
        if (Stopped()) return;
        var stats = _acStats[_acTable[slot]];
        int p1 = 1 << al, m1 = -1 << al;
        // EOBx: after the last coefficient that is already non-zero (G.1.3.3)
        var last = se;
        while (last > 0 && block[ZigZag[last]] == 0) last--;
        for (var k = ss; k <= se; k++)
        {
            var se3 = 3 * (k - 1);
            if (k > last && Decode(stats, se3) != 0) return;
            while (true)
            {
                ref var c = ref block[ZigZag[k]];
                if (c != 0)
                {
                    // correction bit (SC = S0 + 1)
                    if (Decode(stats, se3 + 2) != 0) c = (short)Math.Clamp(c + (c < 0 ? m1 : p1), short.MinValue, short.MaxValue);
                    break;
                }
                if (Decode(stats, se3 + 1) != 0)
                {
                    c = (short)(DecodeFixed() != 0 ? m1 : p1);
                    break;
                }
                se3 += 3;
                if (++k > se)
                {
                    Broken();
                    return;
                }
            }
        }
    }
}
