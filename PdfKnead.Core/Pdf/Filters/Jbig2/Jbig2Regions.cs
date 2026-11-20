namespace PdfKnead.Core.Pdf.Filters.Jbig2;

/// <summary>
/// Work limit against malicious files (nonsensical sizes, endless loops on 0xFF padding). Sized by the image size in the
/// PDF file — a real image needs a few passes over its area — and by the encoded data: every symbol costs a base price, and a
/// dictionary of 262 144 tiny symbols for a small image (a stress test of a reference reader) ran out of a budget sized by the image
/// alone, while 300 bytes of crafted data must still not keep the decoder busy for 40 s (found by fuzzing); so each byte of data buys
/// 64 units of work (docs/FEHLERGEDAECHTNIS.md, No. 437). With a fixed limit of 2 gigapixels there was no bound at all.
/// </summary>
/// <param name="imagePixels">Pixels of the image in the PDF file.</param>
/// <param name="dataLength">Bytes of encoded data (globals and the image's own).</param>
internal sealed class Jbig2Budget(long imagePixels = 1L << 26, long dataLength = 0)
{
    private readonly long _limit = Math.Clamp(16 * imagePixels + (16L << 20) + 64 * Math.Max(0, dataLength), 0, 1L << 31);
    private readonly long _maxBitmap = Math.Clamp(4 * imagePixels + (16L << 20), 0, 1L << 31);
    private long _spent;

    /// <summary>Checks whether a bitmap of this size may be allocated; if it is too large for the image, aborts before the memory is taken.</summary>
    public void Allocate(long width, long height)
    {
        if (width * height > _maxBitmap) throw new Jbig2Exception("JBIG2: Bitmap größer als das Bild");
    }

    /// <summary>Charges work for <paramref name="pixels"/> pixels plus a base price per call (region, symbol, placement); otherwise
    /// millions of empty symbols would cost nothing and keep the decoder busy for 40 s.</summary>
    public void Spend(long pixels)
    {
        _spent += Math.Max(0, pixels) + 64;
        if (_spent > _limit) throw new Jbig2Exception("JBIG2: zu viel Arbeit");
    }
}

/// <summary>Exported symbols of a symbol dictionary, plus the retained contexts if any (ITU-T T.88, 7.4.2.1.1).</summary>
internal sealed record Jbig2SymbolDictionary(List<Jbig2Bitmap> Symbols, byte[]? Gb, byte[]? Gr);

/// <summary>Settings of a text region (ITU-T T.88, 6.4.2); also used for aggregate symbols in the dictionary.</summary>
internal sealed class Jbig2TextParams
{
    public bool Huffman;
    public bool Refine;
    public int Width;
    public int Height;
    public int Instances;
    public int LogStrips;
    public IReadOnlyList<Jbig2Bitmap> Symbols = [];
    public int SymbolCodeLength;
    public Jbig2Huffman? SymbolTable;   // null with Huffman: symbol IDs of fixed length (aggregate in the dictionary)
    public int DefaultPixel;
    public int Operator;
    public bool Transposed;
    public int RefCorner;
    public int DsOffset;
    public int RefineTemplate;
    public int[] RefineAt = [-1, -1, -1, -1];
    public Jbig2Huffman? Fs, Ds, Dt, Rdw, Rdh, Rdx, Rdy, RSize;
}

/// <summary>The decoding procedures of ITU-T T.88, clause 6.</summary>
internal static class Jbig2Regions
{
    // Context for SLTP with TPGDON (6.2.5.7), in the bit order of 6.2.5.3
    private static readonly int[] TpgdonContexts = [0x9B25, 0x0795, 0x00E5, 0x0195];

    /// <summary>Context for SLTP with TPGRON (6.3.5.6) per template, in the bit order of <see cref="Refine"/>.</summary>
    internal static readonly int[] TpgronContexts = [0x0100, 0x0080];

    private static int P(byte[] data, int row, int x, int width) =>
        row >= 0 && (uint)x < (uint)width ? (data[row + (x >> 3)] >> (7 - (x & 7))) & 1 : 0;

    // ------------------------------------------------------------ 6.2 Generic region

    /// <summary>
    /// Generic region, arithmetic (6.2.5). The context is formed per row from three
    /// shift registers (rows −2, −1, 0) and the AT pixels, in the bit order of
    /// Figures 3–6 of the standard; otherwise the fixed SLTP contexts would not match.
    /// </summary>
    public static Jbig2Bitmap Generic(MqDecoder dec, byte[] cx, int width, int height, int template, bool tpgdon,
        Jbig2Bitmap? skip, int[] at, Jbig2Budget budget)
    {
        budget.Allocate(width, height);
        budget.Spend((long)width * height);
        var bm = new Jbig2Bitmap(width, height);
        var data = bm.Data;
        var stride = bm.Stride;
        var atCount = template == 0 ? 4 : 1;
        if (at.Length < atCount * 2) throw new Jbig2Exception("JBIG2: AT-Pixel");
        Span<int> atRow = stackalloc int[4];
        // AT pixels at the usual positions (almost always): they then lie right next to the fixed
        // pixels, and the whole context comes from three shift registers
        var nominal = skip is null && template switch
        {
            0 => at is [3, -1, -3, -1, 2, -2, -2, -2],
            1 => at is [3, -1],
            _ => at is [2, -1],
        };
        var ltp = 0;
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            if (tpgdon)
            {
                ltp ^= dec.Decode(cx, TpgdonContexts[template]);
                if (ltp != 0)
                {
                    // Row equal to the one above (the first one: empty)
                    if (y > 0) Array.Copy(data, row - stride, data, row, stride);
                    continue;
                }
            }
            if (width == 0) continue;
            int r1 = y >= 1 ? row - stride : -1, r2 = y >= 2 ? row - 2 * stride : -1;
            if (nominal)
            {
                switch (template)
                {
                    case 0: NominalRow(dec, cx, data, row, r1, r2, width, 4, 3, 7, 4, 2, 5, 11); break;
                    case 1: NominalRow(dec, cx, data, row, r1, r2, width, 3, 3, 6, 3, 2, 4, 9); break;
                    case 2: NominalRow(dec, cx, data, row, r1, r2, width, 2, 2, 5, 2, 1, 3, 7); break;
                    default: NominalRow(dec, cx, data, row, r1, r2, width, 4, 2, 6, 4, -1, 0, 0); break;
                }
                continue;
            }
            for (var k = 0; k < atCount; k++)
            {
                var ay = y + at[2 * k + 1];
                atRow[k] = ay >= 0 && ay <= y ? ay * stride : -1;
            }
            int a0x = at[0], r0 = atRow[0];
            switch (template)
            {
                case 0:
                {
                    int a1x = at[2], a2x = at[4], a3x = at[6], ra1 = atRow[1], ra2 = atRow[2], ra3 = atRow[3];
                    int w0 = 0, w1 = P(data, r1, 0, width) << 2 | P(data, r1, 1, width) << 1 | P(data, r1, 2, width);
                    var w2 = P(data, r2, 0, width) << 1 | P(data, r2, 1, width);
                    for (var x = 0; x < width; x++)
                    {
                        var bit = skip is not null && skip.Get(x, y) != 0 ? 0 : dec.Decode(cx,
                            w2 << 12 | w1 << 5 | w0 | P(data, r0, x + a0x, width) << 4 | P(data, ra1, x + a1x, width) << 10
                            | P(data, ra2, x + a2x, width) << 11 | P(data, ra3, x + a3x, width) << 15);
                        if (bit != 0) data[row + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                        w0 = (w0 << 1 | bit) & 0xF;
                        w1 = (w1 << 1 | P(data, r1, x + 3, width)) & 0x1F;
                        w2 = (w2 << 1 | P(data, r2, x + 2, width)) & 0x7;
                    }
                    break;
                }
                case 1:
                {
                    int w0 = 0, w1 = P(data, r1, 0, width) << 2 | P(data, r1, 1, width) << 1 | P(data, r1, 2, width);
                    var w2 = P(data, r2, 0, width) << 2 | P(data, r2, 1, width) << 1 | P(data, r2, 2, width);
                    for (var x = 0; x < width; x++)
                    {
                        var bit = skip is not null && skip.Get(x, y) != 0 ? 0
                            : dec.Decode(cx, w2 << 9 | w1 << 4 | P(data, r0, x + a0x, width) << 3 | w0);
                        if (bit != 0) data[row + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                        w0 = (w0 << 1 | bit) & 0x7;
                        w1 = (w1 << 1 | P(data, r1, x + 3, width)) & 0x1F;
                        w2 = (w2 << 1 | P(data, r2, x + 3, width)) & 0xF;
                    }
                    break;
                }
                case 2:
                {
                    int w0 = 0, w1 = P(data, r1, 0, width) << 1 | P(data, r1, 1, width);
                    var w2 = P(data, r2, 0, width) << 1 | P(data, r2, 1, width);
                    for (var x = 0; x < width; x++)
                    {
                        var bit = skip is not null && skip.Get(x, y) != 0 ? 0
                            : dec.Decode(cx, w2 << 7 | w1 << 3 | P(data, r0, x + a0x, width) << 2 | w0);
                        if (bit != 0) data[row + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                        w0 = (w0 << 1 | bit) & 0x3;
                        w1 = (w1 << 1 | P(data, r1, x + 2, width)) & 0xF;
                        w2 = (w2 << 1 | P(data, r2, x + 2, width)) & 0x7;
                    }
                    break;
                }
                default:
                {
                    int w0 = 0, w1 = P(data, r1, 0, width) << 1 | P(data, r1, 1, width);
                    for (var x = 0; x < width; x++)
                    {
                        var bit = skip is not null && skip.Get(x, y) != 0 ? 0
                            : dec.Decode(cx, w1 << 5 | P(data, r0, x + a0x, width) << 4 | w0);
                        if (bit != 0) data[row + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                        w0 = (w0 << 1 | bit) & 0xF;
                        w1 = (w1 << 1 | P(data, r1, x + 2, width)) & 0x1F;
                    }
                    break;
                }
            }
        }
        return bm;
    }

    /// <summary>
    /// Decodes one row with the AT pixels at the usual positions: row −1 as the window [x − (bits1 − right1 − 1), x + right1],
    /// row −2 as [.., x + right2], the current row with <paramref name="bits0"/> pixels to the left of x.
    /// </summary>
    private static void NominalRow(MqDecoder dec, byte[] cx, byte[] data, int row, int r1, int r2, int width,
        int bits0, int right1, int bits1, int shift1, int right2, int bits2, int shift2)
    {
        int m0 = (1 << bits0) - 1, m1 = (1 << bits1) - 1, m2 = (1 << bits2) - 1;
        int w0 = 0, w1 = 0, w2 = 0;
        for (var k = 0; k <= right1; k++) w1 = w1 << 1 | P(data, r1, k, width);
        for (var k = 0; k <= right2; k++) w2 = w2 << 1 | P(data, r2, k, width);
        // The next bits of the rows above come byte by byte from a 24-bit window (bit 23 = x = 8i) instead of P() per pixel; 0 beyond the row.
        // Bits beyond the width in the last byte are 0 in finished rows (only bits within the width are set).
        var stride = (width + 7) >> 3;
        int s1 = 23 - (right1 + 1), s2 = 23 - (right2 + 1);
        for (var i = 0; i < stride; i++)
        {
            int win1 = Window(data, r1, i, stride), win2 = right2 >= 0 ? Window(data, r2, i, stride) : 0;
            var end = Math.Min(8, width - i * 8);
            var b = 0;
            for (var k = 0; k < end; k++)
            {
                var bit = dec.Decode(cx, w2 << shift2 | w1 << shift1 | w0);
                b |= bit << (7 - k);
                w0 = (w0 << 1 | bit) & m0;
                w1 = (w1 << 1 | (win1 >> (s1 - k)) & 1) & m1;
                w2 = (w2 << 1 | (win2 >> (s2 - k)) & 1) & m2;
            }
            data[row + i] |= (byte)b;
        }
    }

    /// <summary>Three bytes of a row from byte <paramref name="i"/> (bit 23 = first pixel), 0 beyond the row; row −1 = all 0.</summary>
    private static int Window(byte[] data, int row, int i, int stride) => row < 0 ? 0
        : data[row + i] << 16 | (i + 1 < stride ? data[row + i + 1] << 8 : 0) | (i + 2 < stride ? data[row + i + 2] : 0);

    /// <summary>Generic region with MMR (6.2.6), using Group 4 from the CCITT filter.</summary>
    public static Jbig2Bitmap Mmr(byte[] data, int start, int end, int width, int height, out int endByte, Jbig2Budget budget)
    {
        budget.Allocate(width, height);
        budget.Spend((long)width * height / 8);
        endByte = start;
        if (width == 0 || height == 0) return new Jbig2Bitmap(width, height);
        return new Jbig2Bitmap(width, height, CcittFax.DecodeMmr(data, start, end, width, height, out endByte));
    }

    // ------------------------------------------------------------ 6.3 Refinement

    /// <summary>
    /// Refinement (6.3.5): a new bitmap from a reference bitmap (shifted by dx, dy) and the
    /// neighbours already decoded. Template 0: 13 context bits with two AT pixels; template 1: 10.
    /// </summary>
    public static Jbig2Bitmap Refine(MqDecoder dec, byte[] cx, int width, int height, int template, Jbig2Bitmap reference,
        int dx, int dy, bool tpgron, int[] at, Jbig2Budget budget)
    {
        budget.Allocate(width, height);
        budget.Spend((long)width * height);
        var bm = new Jbig2Bitmap(width, height);
        var ltp = 0;
        for (var y = 0; y < height; y++)
        {
            if (tpgron) ltp ^= dec.Decode(cx, TpgronContexts[template]);
            var ry = y - dy;
            for (var x = 0; x < width; x++)
            {
                var rx = x - dx;
                if (ltp != 0)
                {
                    // TPGRPIX: neighbourhood in the reference bitmap uniform → copy the pixel, decode nothing
                    var v = reference.Get(rx, ry);
                    if (Uniform(reference, rx, ry, v))
                    {
                        if (v != 0) bm.Set(x, y);
                        continue;
                    }
                }
                int context;
                if (template == 0)
                {
                    context = bm.Get(x - 1, y) | bm.Get(x + 1, y - 1) << 1 | bm.Get(x, y - 1) << 2 | bm.Get(x + at[0], y + at[1]) << 3
                        | reference.Get(rx + 1, ry + 1) << 4 | reference.Get(rx, ry + 1) << 5 | reference.Get(rx - 1, ry + 1) << 6
                        | reference.Get(rx + 1, ry) << 7 | reference.Get(rx, ry) << 8 | reference.Get(rx - 1, ry) << 9
                        | reference.Get(rx + 1, ry - 1) << 10 | reference.Get(rx, ry - 1) << 11 | reference.Get(rx + at[2], ry + at[3]) << 12;
                }
                else
                {
                    context = bm.Get(x - 1, y) | bm.Get(x + 1, y - 1) << 1 | bm.Get(x, y - 1) << 2 | bm.Get(x - 1, y - 1) << 3
                        | reference.Get(rx + 1, ry + 1) << 4 | reference.Get(rx, ry + 1) << 5 | reference.Get(rx + 1, ry) << 6
                        | reference.Get(rx, ry) << 7 | reference.Get(rx - 1, ry) << 8 | reference.Get(rx, ry - 1) << 9;
                }
                if (dec.Decode(cx, context) != 0) bm.Set(x, y);
            }
        }
        return bm;
    }

    private static bool Uniform(Jbig2Bitmap reference, int x, int y, int v)
    {
        for (var j = -1; j <= 1; j++)
        for (var i = -1; i <= 1; i++)
            if (reference.Get(x + i, y + j) != v) return false;
        return true;
    }

    // ------------------------------------------------------------ 6.5 Symbol dictionary

    /// <summary>Decodes a symbol dictionary segment (6.5, 7.4.2).</summary>
    public static Jbig2SymbolDictionary SymbolDictionary(byte[] d, int p, int end, List<Jbig2Bitmap> input, Queue<Jbig2Huffman> custom,
        Jbig2SymbolDictionary? previous, Jbig2Budget budget)
    {
        var flags = Jbig2Read.U16(d, p, end);
        p += 2;
        var huff = (flags & 1) != 0;
        var refagg = (flags & 2) != 0;
        var template = (flags >> 10) & 3;
        var refineTemplate = (flags >> 12) & 1;
        var at = new int[huff ? 0 : template == 0 ? 8 : 2];
        for (var i = 0; i < at.Length; i++) at[i] = Jbig2Read.I8(d, p++, end);
        int[] rat = [-1, -1, -1, -1];
        if (refagg && refineTemplate == 0)
            for (var i = 0; i < 4; i++) rat[i] = Jbig2Read.I8(d, p++, end);
        var numExported = Jbig2Read.Count(Jbig2Read.U32(d, p, end), 1 << 22);
        var numNew = Jbig2Read.Count(Jbig2Read.U32(d, p + 4, end), 1 << 20);
        p += 8;

        Jbig2Huffman? tableDh = null, tableDw = null, tableBmSize = null, tableAgg = null;
        if (huff)
        {
            tableDh = ((flags >> 2) & 3) switch { 0 => Jbig2Huffman.Standard(4), 1 => Jbig2Huffman.Standard(5), 3 => Next(custom), _ => throw Bad() };
            tableDw = ((flags >> 4) & 3) switch { 0 => Jbig2Huffman.Standard(2), 1 => Jbig2Huffman.Standard(3), 3 => Next(custom), _ => throw Bad() };
            tableBmSize = (flags & 0x40) == 0 ? Jbig2Huffman.Standard(1) : Next(custom);
            tableAgg = (flags & 0x80) == 0 ? Jbig2Huffman.Standard(1) : Next(custom);
        }

        var cx = new Jbig2Contexts();
        if ((flags & 0x100) != 0 && previous is { Gb: { } gb, Gr: { } gr })
        {
            // Reuse the contexts of the previous dictionary (7.4.2.2)
            cx.Gb = (byte[])gb.Clone();
            cx.Gr = (byte[])gr.Clone();
        }
        var dec = huff ? null : new MqDecoder(d, p, end);
        var hr = huff ? new Jbig2BitReader(d, p, end) : null;

        var all = new List<Jbig2Bitmap>(input.Count + numNew);
        all.AddRange(input);
        var symbolCodeLength = Jbig2Read.CeilLog2((long)input.Count + numNew);
        var newCount = 0;
        var heightClassHeight = 0;
        var widths = new List<int>();
        while (newCount < numNew)
        {
            budget.Spend(0);   // height classes without a symbol make no progress, so they cost too
            heightClassHeight += huff ? tableDh!.Value(hr!) : dec!.Int(cx.Iadh);
            if (heightClassHeight < 0 || heightClassHeight > 1 << 20) throw Bad();
            int symbolWidth = 0, totalWidth = 0;
            widths.Clear();
            while (true)
            {
                var dw = huff ? tableDw!.Decode(hr!) : dec!.DecodeInt(cx.Iadw);
                if (dw is null) break;
                if (newCount >= numNew) throw Bad();
                symbolWidth += dw.Value;
                if (symbolWidth < 0 || symbolWidth > 1 << 20) throw Bad();
                totalWidth += symbolWidth;
                newCount++;
                if (huff && !refagg)
                {
                    widths.Add(symbolWidth);   // follows as a collective bitmap
                    continue;
                }
                Jbig2Bitmap symbol;
                if (!refagg)
                {
                    symbol = Generic(dec!, cx.Gb, symbolWidth, heightClassHeight, template, false, null, at, budget);
                }
                else
                {
                    var instances = huff ? tableAgg!.Value(hr!) : dec!.Int(cx.Iaai);
                    if (instances <= 0 || instances > 1 << 20) throw Bad();
                    if (instances > 1)
                    {
                        // Aggregate: a small text region made of symbols already known (6.5.8.2.1)
                        var text = new Jbig2TextParams
                        {
                            Huffman = huff, Refine = true, Width = symbolWidth, Height = heightClassHeight, Instances = instances,
                            Symbols = all, SymbolCodeLength = symbolCodeLength, Operator = Jbig2Bitmap.Or, RefCorner = 1,
                            RefineTemplate = refineTemplate, RefineAt = rat,
                            Fs = Jbig2Huffman.Standard(6), Ds = Jbig2Huffman.Standard(8), Dt = Jbig2Huffman.Standard(11),
                            Rdw = Jbig2Huffman.Standard(15), Rdh = Jbig2Huffman.Standard(15), Rdx = Jbig2Huffman.Standard(15),
                            Rdy = Jbig2Huffman.Standard(15), RSize = Jbig2Huffman.Standard(1),
                        };
                        symbol = Text(text, dec, cx, hr, d, end, budget);
                    }
                    else if (huff)
                    {
                        // One refined symbol, refinement coded arithmetically in BMSIZE bytes (6.5.8.2.2)
                        var id = (int)hr!.ReadBits(symbolCodeLength);
                        var rdx = Jbig2Huffman.Standard(15).Value(hr);
                        var rdy = Jbig2Huffman.Standard(15).Value(hr);
                        var size = Jbig2Huffman.Standard(1).Value(hr);
                        hr.Align();
                        var start = hr.BytePosition;
                        var refDec = new MqDecoder(d, start, (int)Math.Min(end, (long)start + size));
                        symbol = Refine(refDec, cx.Gr, symbolWidth, heightClassHeight, refineTemplate, Symbol(all, id), rdx, rdy, false, rat, budget);
                        hr.SkipBytes(size);
                    }
                    else
                    {
                        var id = dec!.DecodeId(cx.Iaid(symbolCodeLength), symbolCodeLength);
                        var rdx = dec.Int(cx.Iardx);
                        var rdy = dec.Int(cx.Iardy);
                        symbol = Refine(dec, cx.Gr, symbolWidth, heightClassHeight, refineTemplate, Symbol(all, id), rdx, rdy, false, rat, budget);
                    }
                }
                all.Add(symbol);
            }

            if (huff && !refagg)
            {
                // Collective bitmap of the height class: uncompressed or MMR, then split into symbols (6.5.9)
                var size = tableBmSize!.Value(hr!);
                hr!.Align();
                var start = hr.BytePosition;
                Jbig2Bitmap collective;
                if (size == 0)
                {
                    budget.Allocate(totalWidth, heightClassHeight);
                    collective = new Jbig2Bitmap(totalWidth, heightClassHeight);
                    var bytes = collective.Data.Length;
                    if ((long)start + bytes > end) throw Bad();
                    Array.Copy(d, start, collective.Data, 0, bytes);
                    collective.ClearPadding();
                    hr.SkipBytes(bytes);
                }
                else
                {
                    collective = Mmr(d, start, (int)Math.Min(end, (long)start + size), totalWidth, heightClassHeight, out _, budget);
                    hr.SkipBytes(size);
                }
                var x = 0;
                foreach (var w in widths)
                {
                    all.Add(collective.Crop(x, 0, w, heightClassHeight));
                    x += w;
                }
            }
        }

        // Exported symbols: alternating runs of "not exported" / "exported" (6.5.10)
        var exported = new List<Jbig2Bitmap>(Math.Min(numExported, all.Count));
        var exportFlag = false;
        var zeroRuns = 0;
        for (var index = 0; index < all.Count;)
        {
            var run = huff ? Jbig2Huffman.Standard(1).Value(hr!) : dec!.Int(cx.Iaex);
            if (run < 0 || run > all.Count - index) throw Bad();
            // Runs of length 0 make no progress: consecutive zero runs caused an endless loop (found by fuzzing)
            zeroRuns = run == 0 ? zeroRuns + 1 : 0;
            if (zeroRuns > 2) throw Bad();
            if (exportFlag) exported.AddRange(all.GetRange(index, run));
            index += run;
            exportFlag = !exportFlag;
        }
        var retain = (flags & 0x200) != 0;
        return new Jbig2SymbolDictionary(exported, retain ? cx.Gb : null, retain ? cx.Gr : null);
    }

    private static Jbig2Bitmap Symbol(IReadOnlyList<Jbig2Bitmap> symbols, int id) =>
        (uint)id < (uint)symbols.Count ? symbols[id] : throw Bad();

    private static Jbig2Huffman Next(Queue<Jbig2Huffman> custom) => custom.Count > 0 ? custom.Dequeue() : throw Bad();

    private static Jbig2Exception Bad() => new("JBIG2: ungültige Daten");

    // ------------------------------------------------------------ 6.4 Text region

    /// <summary>
    /// Text region (6.4.5): places symbols side by side in strips, optionally refined.
    /// Arithmetic via <paramref name="dec"/> or Huffman via <paramref name="hr"/>.
    /// </summary>
    public static Jbig2Bitmap Text(Jbig2TextParams p, MqDecoder? dec, Jbig2Contexts cx, Jbig2BitReader? hr, byte[] data, int dataEnd, Jbig2Budget budget)
    {
        budget.Allocate(p.Width, p.Height);
        var bm = new Jbig2Bitmap(p.Width, p.Height, p.DefaultPixel);
        var strips = 1 << p.LogStrips;
        int Value(Jbig2Huffman? table, byte[] contexts) => p.Huffman ? table!.Value(hr!) : dec!.Int(contexts);

        var stripT = -(long)Value(p.Dt, cx.Iadt) * strips;
        var firstS = 0L;
        var instances = 0;
        while (instances < p.Instances)
        {
            stripT += (long)Value(p.Dt, cx.Iadt) * strips;
            var first = true;
            var curS = 0L;
            while (true)
            {
                if (first)
                {
                    firstS += Value(p.Fs, cx.Iafs);
                    curS = firstS;
                    first = false;
                }
                else
                {
                    var ds = p.Huffman ? p.Ds!.Decode(hr!) : dec!.DecodeInt(cx.Iads);
                    if (ds is null) break;   // OOB: end of the strip
                    if (instances >= p.Instances) break;
                    curS += ds.Value + p.DsOffset;
                }

                var curT = strips == 1 ? 0 : p.Huffman ? (int)hr!.ReadBits(p.LogStrips) : dec!.Int(cx.Iait);
                var t = stripT + curT;
                var id = p.Huffman
                    ? p.SymbolTable is { } table ? table.Value(hr!) : (int)hr!.ReadBits(p.SymbolCodeLength)
                    : dec!.DecodeId(cx.Iaid(p.SymbolCodeLength), p.SymbolCodeLength);
                var symbol = Symbol(p.Symbols, id);

                if (p.Refine && (p.Huffman ? hr!.ReadBit() : dec!.Int(cx.Iari)) != 0)
                {
                    var rdw = Value(p.Rdw, cx.Iardw);
                    var rdh = Value(p.Rdh, cx.Iardh);
                    var rdx = Value(p.Rdx, cx.Iardx);
                    var rdy = Value(p.Rdy, cx.Iardy);
                    int w = symbol.Width + rdw, h = symbol.Height + rdh;
                    if (w < 0 || h < 0) throw Bad();
                    int gdx = (rdw >> 1) + rdx, gdy = (rdh >> 1) + rdy;
                    if (p.Huffman)
                    {
                        var size = p.RSize!.Value(hr!);
                        hr!.Align();
                        var start = hr.BytePosition;
                        var refDec = new MqDecoder(data, start, (int)Math.Min(dataEnd, (long)start + size));
                        symbol = Refine(refDec, cx.Gr, w, h, p.RefineTemplate, symbol, gdx, gdy, false, p.RefineAt, budget);
                        hr.SkipBytes(size);
                    }
                    else
                    {
                        symbol = Refine(dec!, cx.Gr, w, h, p.RefineTemplate, symbol, gdx, gdy, false, p.RefineAt, budget);
                    }
                }

                // Reference corner: 0 bottom left, 1 top left, 2 bottom right, 3 top right
                int wi = symbol.Width, hi = symbol.Height;
                var right = p.RefCorner >= 2;
                var bottom = (p.RefCorner & 1) == 0;
                if (!p.Transposed && right) curS += wi - 1;
                else if (p.Transposed && bottom) curS += hi - 1;
                long x = p.Transposed ? t : curS, y = p.Transposed ? curS : t;
                if (right) x -= wi - 1;
                if (bottom) y -= hi - 1;
                budget.Spend((long)wi * hi / 8 + 1);
                bm.Compose(symbol, x, y, p.Operator);
                if (!p.Transposed && !right) curS += wi - 1;
                else if (p.Transposed && !bottom) curS += hi - 1;
                instances++;
            }
        }
        return bm;
    }

    /// <summary>Reads the run-length coded Huffman table of the symbol IDs of a text region (7.4.3.1.7).</summary>
    public static Jbig2Huffman SymbolIdTable(Jbig2BitReader hr, int symbols)
    {
        var runLengths = new int[35];
        for (var i = 0; i < runLengths.Length; i++) runLengths[i] = (int)hr.ReadBits(4);
        var runTable = Jbig2Huffman.FromLengths(runLengths);
        var lengths = new int[symbols];
        for (var i = 0; i < symbols;)
        {
            var code = runTable.Value(hr);
            if (code < 32)
            {
                lengths[i++] = code;
                continue;
            }
            int length, count;
            switch (code)
            {
                case 32:
                    if (i == 0) throw Bad();
                    length = lengths[i - 1];
                    count = 3 + (int)hr.ReadBits(2);
                    break;
                case 33:
                    length = 0;
                    count = 3 + (int)hr.ReadBits(3);
                    break;
                default:
                    length = 0;
                    count = 11 + (int)hr.ReadBits(7);
                    break;
            }
            if (count > symbols - i) throw Bad();
            for (var k = 0; k < count; k++) lengths[i++] = length;
        }
        hr.Align();
        return Jbig2Huffman.FromLengths(lengths);
    }

    // ------------------------------------------------------------ 6.7 Patterns, 6.6 Halftone

    /// <summary>Decodes a pattern dictionary (6.7): the collective bitmap split into GRAYMAX + 1 patterns.</summary>
    public static List<Jbig2Bitmap> PatternDictionary(byte[] d, int p, int end, Jbig2Budget budget)
    {
        var flags = Jbig2Read.U8(d, p, end);
        var mmr = (flags & 1) != 0;
        var template = (flags >> 1) & 3;
        var pw = Jbig2Read.U8(d, p + 1, end);
        var ph = Jbig2Read.U8(d, p + 2, end);
        var grayMax = Jbig2Read.Count(Jbig2Read.U32(d, p + 3, end), 1 << 16);
        p += 7;
        var width = (grayMax + 1) * pw;
        int[] at = template == 0 ? [-pw, 0, -3, -1, 2, -2, -2, -2] : [-pw, 0];
        var collective = mmr
            ? Mmr(d, p, end, width, ph, out _, budget)
            : Generic(new MqDecoder(d, p, end), new byte[1 << 16], width, ph, template, false, null, at, budget);
        var patterns = new List<Jbig2Bitmap>(grayMax + 1);
        for (var i = 0; i <= grayMax; i++) patterns.Add(collective.Crop(i * pw, 0, pw, ph));
        return patterns;
    }

    /// <summary>Decodes a halftone region (6.6).</summary>
    public static Jbig2Bitmap Halftone(byte[] d, int p, int end, int width, int height,
        List<Jbig2Bitmap> patterns, Jbig2Budget budget)
    {
        var flags = Jbig2Read.U8(d, p, end);
        var mmr = (flags & 1) != 0;
        var template = (flags >> 1) & 3;
        var enableSkip = (flags & 8) != 0;
        var op = (flags >> 4) & 7;
        var defaultPixel = (flags >> 7) & 1;
        var gw = Jbig2Read.Count(Jbig2Read.U32(d, p + 1, end), 1 << 20);
        var gh = Jbig2Read.Count(Jbig2Read.U32(d, p + 5, end), 1 << 20);
        long gx = Jbig2Read.I32(d, p + 9, end), gy = Jbig2Read.I32(d, p + 13, end);
        long rx = Jbig2Read.U16(d, p + 17, end), ry = Jbig2Read.U16(d, p + 19, end);
        p += 21;
        if (patterns.Count == 0) throw Bad();
        budget.Spend((long)gw * gh);
        budget.Allocate(width, height);
        budget.Allocate(gw, gh);

        var hbm = new Jbig2Bitmap(width, height, defaultPixel);
        int pw = patterns[0].Width, ph = patterns[0].Height;
        Jbig2Bitmap? skip = null;
        if (enableSkip)
        {
            // Do not decode grid points whose pattern would lie completely outside (6.6.5.1)
            skip = new Jbig2Bitmap(gw, gh);
            for (var mg = 0; mg < gh; mg++)
            for (var ng = 0; ng < gw; ng++)
            {
                long x = (gx + mg * ry + ng * rx) >> 8, y = (gy + mg * rx - ng * ry) >> 8;
                if (x + pw <= 0 || x >= width || y + ph <= 0 || y >= height) skip.Set(ng, mg);
            }
        }

        // Greyscale image (C.5): bit planes from the most significant bit down, Gray code
        var bpp = Jbig2Read.CeilLog2(patterns.Count);
        var planes = new Jbig2Bitmap[bpp];
        var dec = mmr ? null : new MqDecoder(d, p, end);
        var gb = new byte[1 << 16];
        int[] at = template switch { 0 => [3, -1, -3, -1, 2, -2, -2, -2], 1 => [3, -1], _ => [2, -1] };
        var position = p;
        for (var j = bpp - 1; j >= 0; j--)
            planes[j] = mmr
                ? Mmr(d, position, end, gw, gh, out position, budget)
                : Generic(dec!, gb, gw, gh, template, false, skip, at, budget);
        for (var j = bpp - 2; j >= 0; j--)
        {
            var target = planes[j].Data;
            var source = planes[j + 1].Data;
            for (var i = 0; i < target.Length; i++) target[i] ^= source[i];
        }

        for (var mg = 0; mg < gh; mg++)
        for (var ng = 0; ng < gw; ng++)
        {
            var value = 0;
            for (var j = 0; j < bpp; j++) value |= planes[j].Get(ng, mg) << j;
            var pattern = patterns[Math.Min(value, patterns.Count - 1)];
            long x = (gx + mg * ry + ng * rx) >> 8, y = (gy + mg * rx - ng * ry) >> 8;
            budget.Spend((long)pw * ph / 8 + 1);
            hbm.Compose(pattern, x, y, op);
        }
        return hbm;
    }
}
