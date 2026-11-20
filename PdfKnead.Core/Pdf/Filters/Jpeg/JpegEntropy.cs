namespace PdfKnead.Core.Pdf.Filters.Jpeg;

/// <summary>
/// Entropy decoding of one scan (ITU-T T.81, Annex F for sequential, G for progressive): Huffman (<see cref="JpegHuffman"/>) or
/// arithmetic (<see cref="JpegArithmetic"/>). Coefficients are written quantised, in natural order; a block whose data is damaged is
/// left with what was decoded so far, and <see cref="Damaged"/> is set.
/// </summary>
internal abstract class JpegEntropy
{
    /// <summary>Position in natural order of the k-th coefficient in zig-zag order (T.81, Figure A.6).</summary>
    public static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    /// <summary>The data of the scan held invalid codes or ended before the scan did.</summary>
    public bool Damaged { get; protected set; }

    /// <summary>Start of the scan and after every restart marker: predictions and runs from zero (T.81, F.2.1.3.1, G.1.2.2).</summary>
    public abstract void Reset();

    /// <summary>Consumes the restart marker <paramref name="number"/>; false if the scan ends instead.</summary>
    public abstract bool Restart(int number);

    /// <summary>Where the next marker after the scan starts.</summary>
    public abstract int End();

    /// <summary>Nothing more can be decoded until the next restart marker (damaged data, or the data of the scan used up).</summary>
    public abstract bool Dead { get; }

    /// <summary>A whole block of a sequential scan (T.81, F.2.2).</summary>
    /// <param name="slot">The component's place in the scan.</param>
    public abstract void Sequential(int slot, Span<short> block);

    /// <summary>First DC scan of a progressive image (G.1.2.1): the DC coefficient shifted by <paramref name="al"/>.</summary>
    public abstract void DcFirst(int slot, Span<short> block, int al);

    /// <summary>Refining DC scan (G.1.2.1): one more bit at <paramref name="al"/>.</summary>
    public abstract void DcRefine(Span<short> block, int al);

    /// <summary>First AC scan of the band <paramref name="ss"/>–<paramref name="se"/> (G.1.2.2).</summary>
    public abstract void AcFirst(int slot, Span<short> block, int ss, int se, int al);

    /// <summary>Refining AC scan of the band (G.1.2.3).</summary>
    public abstract void AcRefine(int slot, Span<short> block, int ss, int se, int al);

    /// <summary>A coefficient shifted by <paramref name="al"/>, kept within 16 bits (only damaged data comes near the limits).</summary>
    protected static short Shifted(int value, int al)
    {
        var v = (long)value << al;
        return (short)Math.Clamp(v, short.MinValue, short.MaxValue);
    }
}

/// <summary>Huffman decoding (T.81, F.2.2 and G.1.2).</summary>
internal sealed class JpegHuffman(JpegBits bits, JpegHuffmanTable?[] dc, JpegHuffmanTable?[] ac) : JpegEntropy
{
    private readonly int[] _prediction = new int[4];
    private int _eobRun;
    private bool _broken;   // an invalid code: zeros until the next restart marker

    public bool RanShort => bits.RanShort;
    public bool Skipped => bits.Skipped;

    public override void Reset()
    {
        Array.Clear(_prediction);
        _eobRun = 0;
        _broken = false;
    }

    public override bool Restart(int number)
    {
        Damaged |= bits.RanShort;
        var ok = bits.Restart(number);
        Damaged |= bits.Skipped;
        Reset();
        return ok;
    }

    public override int End()
    {
        Damaged |= bits.RanShort;
        return bits.End();
    }

    public override bool Dead => Stopped();

    /// <summary>
    /// Whether decoding stops until the next restart marker: after an invalid code, or once bits past the end of the data were used — the
    /// blocks after that keep zero coefficients (grey), as common decoders leave them, instead of values decoded from padding.
    /// </summary>
    private bool Stopped()
    {
        if (!_broken && bits.RanShort)
        {
            _broken = true;
            Damaged = true;
        }
        return _broken;
    }

    private int Symbol(JpegHuffmanTable? table)
    {
        if (Stopped() || table is null) return -1;
        var s = bits.Decode(table);
        if (s < 0)
        {
            _broken = true;
            Damaged = true;
        }
        return s;
    }

    public override void Sequential(int slot, Span<short> block)
    {
        var t = Symbol(dc[slot]);
        if (t < 0) return;
        // DC categories up to 11 for 8-bit samples (F.1.2.1.1); larger ones only in damaged data, still read as bits
        _prediction[slot] += bits.Receive(Math.Min(t, 16));
        block[0] = (short)Math.Clamp(_prediction[slot], short.MinValue, short.MaxValue);
        var table = ac[slot];
        if (table is null)
        {
            _broken = true;
            Damaged = true;
            return;
        }
        var fastAc = table.FastAc;
        for (var k = 1; k < 64;)
        {
            // most coefficients: code and magnitude in one lookup
            var e = fastAc[bits.PeekFast()];
            if (e != 0)
            {
                k += (e >> 4) & 15;
                if (k > 63)
                {
                    _broken = true;
                    Damaged = true;
                    return;
                }
                bits.Skip(e & 15);
                block[ZigZag[k]] = (short)(e >> 8);
                k++;
                continue;
            }
            var rs = bits.Decode(table);
            if (rs < 0)
            {
                _broken = true;
                Damaged = true;
                return;
            }
            if (rs < 0) return;
            int r = rs >> 4, s = rs & 15;
            if (s == 0)
            {
                if (r != 15) return;   // end of block
                k += 16;
                continue;
            }
            k += r;
            if (k > 63)
            {
                _broken = true;
                Damaged = true;
                return;
            }
            block[ZigZag[k]] = (short)bits.Receive(s);
            k++;
        }
    }

    public override void DcFirst(int slot, Span<short> block, int al)
    {
        var t = Symbol(dc[slot]);
        if (t < 0) return;
        _prediction[slot] += bits.Receive(Math.Min(t, 16));
        block[0] = Shifted(_prediction[slot], al);
    }

    public override void DcRefine(Span<short> block, int al)
    {
        if (Stopped()) return;
        if (bits.Bit() != 0) block[0] = (short)((ushort)block[0] | 1 << al);
    }

    public override void AcFirst(int slot, Span<short> block, int ss, int se, int al)
    {
        if (Stopped()) return;
        if (_eobRun > 0)
        {
            _eobRun--;
            return;
        }
        var table = ac[slot];
        for (var k = ss; k <= se;)
        {
            var rs = Symbol(table);
            if (rs < 0) return;
            int r = rs >> 4, s = rs & 15;
            if (s == 0)
            {
                if (r < 15)
                {
                    // end of band in this block and the next EOBRUN − 1 (G.1.2.2, Table G.1)
                    _eobRun = (1 << r) - 1 + bits.Read(r);
                    return;
                }
                k += 16;
                continue;
            }
            k += r;
            if (k > se)
            {
                _broken = true;
                Damaged = true;
                return;
            }
            block[ZigZag[k]] = Shifted(bits.Receive(s), al);
            k++;
        }
    }

    public override void AcRefine(int slot, Span<short> block, int ss, int se, int al)
    {
        if (Stopped()) return;
        int p1 = 1 << al, m1 = -1 << al;
        var k = ss;
        if (_eobRun == 0)
        {
            var table = ac[slot];
            for (; k <= se; k++)
            {
                var rs = Symbol(table);
                if (rs < 0) return;
                int r = rs >> 4, s = rs & 15, value = 0;
                if (s != 0)
                {
                    // a new coefficient is always ±1 at this bit (G.1.2.3); other sizes only in damaged data
                    value = bits.Bit() != 0 ? p1 : m1;
                }
                else if (r != 15)
                {
                    _eobRun = (1 << r) + bits.Read(r);
                    break;
                }
                // pass over coefficients already non-zero (each gets its correction bit) and r zero ones
                for (; k <= se; k++)
                {
                    ref var c = ref block[ZigZag[k]];
                    if (c != 0)
                    {
                        Correct(ref c, p1, m1);
                    }
                    else
                    {
                        if (r == 0) break;
                        r--;
                    }
                }
                if (value != 0 && k <= se) block[ZigZag[k]] = (short)value;
            }
        }
        if (_eobRun > 0)
        {
            // the rest of the band: only correction bits for the coefficients already non-zero
            for (; k <= se; k++)
            {
                ref var c = ref block[ZigZag[k]];
                if (c != 0) Correct(ref c, p1, m1);
            }
            _eobRun--;
        }
    }

    private void Correct(ref short c, int p1, int m1)
    {
        if (bits.Bit() == 0 || (c & p1) != 0) return;
        c = (short)Math.Clamp(c + (c >= 0 ? p1 : m1), short.MinValue, short.MaxValue);
    }
}
