namespace PdfKnead.Core.Pdf.Filters.Jpx;

/// <summary>Codeword segment of a code-block: coding passes and data pieces (from one or more packets).</summary>
internal sealed class JpxSegment
{
    public int Passes;
    public int MaxPasses;
    public List<(byte[] Data, int Start, int Length)>? Pieces;
    public int Length;

    public byte[] Join()
    {
        if (Pieces is null) return [];
        if (Pieces.Count == 1 && Pieces[0].Start == 0 && Pieces[0].Length == Pieces[0].Data.Length) return Pieces[0].Data;
        var all = new byte[Length];
        var at = 0;
        foreach (var (data, start, length) in Pieces)
        {
            Buffer.BlockCopy(data, start, all, at, length);
            at += length;
        }
        return all;
    }
}

internal sealed class JpxCodeBlock
{
    public int X0, Y0, X1, Y1;
    public bool Included;
    public int ZeroPlanes;
    public int Lblock = 3;
    public List<JpxSegment>? Segments;
}

/// <summary>Code-blocks of a sub-band within a precinct, with the two tag trees.</summary>
internal sealed class JpxPrecinctBand(int blocksX, int blocksY, JpxCodeBlock[] blocks)
{
    public readonly int BlocksX = blocksX;
    public readonly JpxCodeBlock[] Blocks = blocks;
    public readonly JpxTagTree Inclusion = new(blocksX, blocksY);
    public readonly JpxTagTree ZeroPlanes = new(blocksX, blocksY);
}

internal sealed class JpxBand
{
    /// <summary>0 LL, 1 HL, 2 LH, 3 HH.</summary>
    public int Orientation;
    public int X0, Y0, X1, Y1;

    /// <summary>Precinct and code-block size in the sub-band (powers of two).</summary>
    public int PrecinctWidth, PrecinctHeight, BlockWidth, BlockHeight;

    /// <summary>Quantisation step (9/7) and number of magnitude bit planes Mb.</summary>
    public float Step;
    public int MagnitudeBits;

    /// <summary>Position in the buffer of the tile-component (the sub-bands lie there in quadrants).</summary>
    public int OffsetX, OffsetY;
}

internal sealed class JpxResolution
{
    public int X0, Y0, X1, Y1;
    public int PrecinctWidth, PrecinctHeight;
    public int PrecinctsX, PrecinctsY, FirstPrecinctX, FirstPrecinctY;
    public JpxBand[] Bands = [];
    public JpxPrecinctBand?[]?[] Precincts = [];
    public int[] NextLayer = [];

    public int Width => X1 - X0;
    public int Height => Y1 - Y0;
    public int PrecinctCount => PrecinctsX * PrecinctsY;
}

internal sealed class JpxTileComponent
{
    public int Component;
    public int X0, Y0, X1, Y1;
    public JpxCoding Coding = null!;
    public JpxQuantization Quantization = null!;
    public int Roi;
    public JpxResolution[] Resolutions = [];

    /// <summary>Highest decoded resolution (less than <see cref="JpxCoding.Levels"/> when decoding at reduced size).</summary>
    public int Keep;

    /// <summary>Coefficients; after the wavelet transform, the samples of resolution <see cref="Keep"/>.</summary>
    public float[] Data = [];
}

/// <summary>
/// Decodes one tile (tier 2 and reconstruction): read packet headers (B.10), collect code-block data, decode the blocks,
/// dequantise, inverse wavelet transform (F.3) and component transform (G). As in common implementations, the sub-bands lie in
/// quadrants in the buffer of the tile-component; the wavelet transform works in it level by level.
/// </summary>
internal sealed class JpxTile
{
    /// <summary>Limits against crafted files: precincts per tile, code-blocks in total, iterations of the packet loop.</summary>
    private const int MaxPrecincts = 1 << 22, MaxBlocks = 1 << 24, MaxPacketSteps = 50_000_000;

    private readonly JpxCodestream _cs;
    private readonly JpxTileData _data;
    private readonly int _reduce;
    private readonly JpxDecodeState _state;
    private int _tx0, _ty0, _tx1, _ty1;
    private JpxTileComponent[] _components = [];
    private int _layers;
    private int _order;
    private bool _mct;
    private int _componentTransform;
    private byte[] _body = [];
    private int _bodyPos;
    private JpxBits? _headers;
    private int _blocks;

    public JpxTile(JpxCodestream cs, int index, JpxTileData data, int reduce, JpxDecodeState state)
    {
        _cs = cs;
        _data = data;
        _reduce = reduce;
        _state = state;
        var p = index % cs.TilesX;
        var q = index / cs.TilesX;
        _tx0 = (int)Math.Max((long)cs.TileX0 + (long)p * cs.TileWidth, cs.X0);
        _ty0 = (int)Math.Max((long)cs.TileY0 + (long)q * cs.TileHeight, cs.Y0);
        _tx1 = (int)Math.Min((long)cs.TileX0 + (long)(p + 1) * cs.TileWidth, cs.X1);
        _ty1 = (int)Math.Min((long)cs.TileY0 + (long)(q + 1) * cs.TileHeight, cs.Y1);
    }

    public JpxTileComponent[] Components => _components;
    public bool MultipleComponentTransform => _mct;

    /// <summary>The multiple component transformation field of SGcod for this tile (ISO/IEC 15444-2, Table A.8).</summary>
    public int ComponentTransform => _componentTransform;

    /// <summary>The values of the tile's own header (first tile-part), among them the marker segments of ISO/IEC 15444-2.</summary>
    public JpxHeaderValues OwnHeader => _data.Header;

    public static long CeilDiv(long a, long b) => a >= 0 ? (a + b - 1) / b : -(-a / b);

    // ------------------------------------------------------------ Structure

    public void Decode()
    {
        Setup();
        ReadPackets();
        DecodeBlocks();
        foreach (var tc in _components) JpxWavelet.Inverse(tc);
    }

    private void Setup()
    {
        var main = _cs.Main;
        var tile = _data.Header;
        var header = tile.HasCod ? tile : main;
        _layers = Math.Max(1, header.Layers);
        _order = header.Order;
        _mct = header.MultipleComponentTransform;
        _componentTransform = header.ComponentTransform;

        _components = new JpxTileComponent[_cs.Components.Length];
        long precincts = 0;
        for (var c = 0; c < _components.Length; c++)
        {
            var comp = _cs.Components[c];
            var coding = tile.ComponentCoding[c] ?? tile.Coding ?? main.ComponentCoding[c] ?? main.Coding!;
            var quant = tile.ComponentQuantization[c] ?? tile.Quantization ?? main.ComponentQuantization[c] ?? main.Quantization!;
            var tc = new JpxTileComponent
            {
                Component = c,
                X0 = (int)CeilDiv(_tx0, comp.Dx),
                Y0 = (int)CeilDiv(_ty0, comp.Dy),
                X1 = (int)CeilDiv(_tx1, comp.Dx),
                Y1 = (int)CeilDiv(_ty1, comp.Dy),
                Coding = coding,
                Quantization = quant,
                Roi = Math.Min(30, tile.RoiShift[c] ?? main.RoiShift[c] ?? 0),
            };
            var levels = coding.Levels;
            tc.Keep = levels - Math.Min(_reduce, levels);
            tc.Resolutions = new JpxResolution[levels + 1];
            for (var r = 0; r <= levels; r++)
            {
                var res = BuildResolution(tc, comp, r);
                tc.Resolutions[r] = res;
                precincts += res.PrecinctCount;
                if (precincts > MaxPrecincts) throw new JpxException("JPX: zu viele Bereiche");
            }
            var top = tc.Resolutions[tc.Keep];
            var size = (long)top.Width * top.Height;
            if (size > Array.MaxLength) throw new JpxException("JPX: Kachel zu groß");
            _state.Reserve(size * 4);
            tc.Data = new float[size];
            _components[c] = tc;
        }
    }

    private JpxResolution BuildResolution(JpxTileComponent tc, JpxComponent comp, int r)
    {
        var levels = tc.Coding.Levels;
        var shift = levels - r;
        var res = new JpxResolution
        {
            X0 = (int)CeilDiv(tc.X0, 1L << shift),
            Y0 = (int)CeilDiv(tc.Y0, 1L << shift),
            X1 = (int)CeilDiv(tc.X1, 1L << shift),
            Y1 = (int)CeilDiv(tc.Y1, 1L << shift),
            PrecinctWidth = tc.Coding.PrecinctWidth(r),
            PrecinctHeight = tc.Coding.PrecinctHeight(r),
        };
        if (r > 0 && (res.PrecinctWidth == 0 || res.PrecinctHeight == 0)) throw new JpxException("JPX: Bereichsgröße");
        if (res.X1 > res.X0 && res.Y1 > res.Y0)
        {
            res.FirstPrecinctX = res.X0 >> res.PrecinctWidth;
            res.FirstPrecinctY = res.Y0 >> res.PrecinctHeight;
            var px = CeilDiv(res.X1, 1L << res.PrecinctWidth) - res.FirstPrecinctX;
            var py = CeilDiv(res.Y1, 1L << res.PrecinctHeight) - res.FirstPrecinctY;
            if (px * py > MaxPrecincts) throw new JpxException("JPX: zu viele Bereiche");
            res.PrecinctsX = (int)px;
            res.PrecinctsY = (int)py;
        }
        res.Precincts = new JpxPrecinctBand?[]?[res.PrecinctCount];
        res.NextLayer = new int[res.PrecinctCount];

        var precision = comp.Precision;
        var quant = tc.Quantization;
        if (r == 0)
        {
            res.Bands = [Band(tc, res, 0, levels, 0, precision, quant, r)];
        }
        else
        {
            res.Bands = new JpxBand[3];
            for (var o = 1; o <= 3; o++) res.Bands[o - 1] = Band(tc, res, o, levels - r + 1, 1 + 3 * (r - 1) + o - 1, precision, quant, r);
        }
        return res;
    }

    private static JpxBand Band(JpxTileComponent tc, JpxResolution res, int orientation, int nb, int quantIndex, int precision, JpxQuantization quant, int r)
    {
        var band = new JpxBand { Orientation = orientation };
        if (orientation == 0)
        {
            (band.X0, band.Y0, band.X1, band.Y1) = (res.X0, res.Y0, res.X1, res.Y1);
        }
        else
        {
            long xo = orientation & 1, yo = orientation >> 1;
            var half = 1L << (nb - 1);
            var full = 1L << nb;
            band.X0 = (int)CeilDiv(tc.X0 - half * xo, full);
            band.Y0 = (int)CeilDiv(tc.Y0 - half * yo, full);
            band.X1 = (int)CeilDiv(tc.X1 - half * xo, full);
            band.Y1 = (int)CeilDiv(tc.Y1 - half * yo, full);
        }
        band.PrecinctWidth = r == 0 ? res.PrecinctWidth : res.PrecinctWidth - 1;
        band.PrecinctHeight = r == 0 ? res.PrecinctHeight : res.PrecinctHeight - 1;
        band.BlockWidth = Math.Min(tc.Coding.BlockWidth, band.PrecinctWidth);
        band.BlockHeight = Math.Min(tc.Coding.BlockHeight, band.PrecinctHeight);

        var (exponent, mantissa) = quant.Band(quantIndex, tc.Coding.Levels, orientation == 0 ? tc.Coding.Levels : nb);
        band.MagnitudeBits = quant.GuardBits + exponent - 1;
        // Gain of the sub-band: LL 0, HL/LH 1, HH 2 (E-3)
        var gain = orientation == 0 ? 0 : orientation == 3 ? 2 : 1;
        band.Step = tc.Coding.Reversible ? 1f : (float)(Math.Pow(2, precision + gain - exponent) * (1 + mantissa / 2048.0));
        return band;
    }

    // ------------------------------------------------------------ Packets

    private void ReadPackets()
    {
        // Concatenate the tile-parts; packet headers from PPM/PPT, otherwise in the codestream
        var length = _data.Bodies.Sum(b => (long)(b.End - b.Start));
        if (length > Array.MaxLength) throw new JpxException("JPX: Kachel zu groß");
        _body = new byte[length];
        var at = 0;
        foreach (var (start, end) in _data.Bodies)
        {
            Buffer.BlockCopy(_cs.Data, start, _body, at, end - start);
            at += end - start;
        }
        byte[]? headers = null;
        if (_data.PpmChunks.Count > 0) headers = [.. _data.PpmChunks.SelectMany(c => c)];
        else if (_data.Ppt.Count > 0) headers = [.. _data.Ppt.OrderBy(p => p.Index).SelectMany(p => p.Data)];
        if (headers is not null) _headers = new JpxBits(headers, 0, headers.Length);

        try
        {
            var steps = 0;
            foreach (var (layer, c, r, p) in Packets())
            {
                if (++steps > MaxPacketSteps || Exhausted) break;
                ReadPacket(layer, _components[c], r, p);
            }
        }
        catch (Exception e) when (e is JpxException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            // Damaged packet data: what has been read so far is drawn
            _state.Truncated = true;
        }
    }

    private bool Exhausted => _bodyPos >= _body.Length && (_headers is null || _headers.Ended);

    private readonly List<(JpxSegment Segment, int Length)> _contributions = [];

    private void ReadPacket(int layer, JpxTileComponent tc, int r, int precinctIndex)
    {
        var res = tc.Resolutions[r];
        // SOP before the packet (even if COD does not announce it)
        if (_bodyPos + 6 <= _body.Length && _body[_bodyPos] == 0xFF && _body[_bodyPos + 1] == 0x91) _bodyPos += 6;
        var bits = _headers ?? new JpxBits(_body, _bodyPos, _body.Length);
        var precinct = res.Precincts[precinctIndex] ??= BuildPrecinct(res, precinctIndex);
        _contributions.Clear();
        var style = tc.Coding.Style;

        if (bits.Bit() != 0)
        {
            foreach (var band in precinct)
            {
                if (band is null) continue;
                for (var k = 0; k < band.Blocks.Length; k++)
                {
                    var block = band.Blocks[k];
                    int x = k % band.BlocksX, y = k / band.BlocksX;
                    var included = block.Included ? bits.Bit() != 0 : band.Inclusion.Decode(bits, x, y, layer + 1);
                    if (!included) continue;
                    if (!block.Included)
                    {
                        block.ZeroPlanes = band.ZeroPlanes.Value(bits, x, y, 74);
                        block.Included = true;
                    }
                    var passes = Passes(bits);
                    while (bits.Bit() != 0)
                        if (++block.Lblock > 32) throw new JpxException("JPX: Lblock");

                    block.Segments ??= [];
                    var segment = block.Segments.Count > 0 && block.Segments[^1].Passes < block.Segments[^1].MaxPasses ? block.Segments[^1] : null;
                    while (passes > 0)
                    {
                        if (segment is null)
                        {
                            segment = new JpxSegment { MaxPasses = MaxPasses(style, block.Segments) };
                            block.Segments.Add(segment);
                        }
                        var take = Math.Min(segment.MaxPasses - segment.Passes, passes);
                        var lengthBits = block.Lblock + FloorLog2(take);
                        if (lengthBits > 31) throw new JpxException("JPX: Segmentlänge");
                        _contributions.Add((segment, bits.Bits(lengthBits)));
                        segment.Passes += take;
                        passes -= take;
                        if (segment.Passes >= segment.MaxPasses) segment = null;
                    }
                }
            }
        }
        bits.Align();
        bits.SkipMarker(0xFF92);
        if (_headers is null) _bodyPos = bits.Position;

        var keep = r <= tc.Keep;
        foreach (var (segment, length) in _contributions)
        {
            var available = Math.Max(0, Math.Min(length, _body.Length - _bodyPos));
            if (available < length) _state.Truncated = true;
            if (keep && available > 0)
            {
                (segment.Pieces ??= []).Add((_body, _bodyPos, available));
                segment.Length += available;
            }
            _bodyPos += available;
        }
    }

    private static int Passes(JpxBits bits)
    {
        if (bits.Bit() == 0) return 1;
        if (bits.Bit() == 0) return 2;
        var v = bits.Bits(2);
        if (v < 3) return 3 + v;
        v = bits.Bits(5);
        if (v < 31) return 6 + v;
        return 37 + bits.Bits(7);
    }

    private static int MaxPasses(int style, List<JpxSegment> segments)
    {
        if ((style & JpxBlockDecoder.TerminateAll) != 0) return 1;
        if ((style & JpxBlockDecoder.Bypass) != 0)
        {
            if (segments.Count == 0) return 10;
            return segments[^1].MaxPasses is 1 or 10 ? 2 : 1;
        }
        return 109;
    }

    private static int FloorLog2(int v) => 31 - int.LeadingZeroCount(v);

    private JpxPrecinctBand?[] BuildPrecinct(JpxResolution res, int index)
    {
        var kx = (long)res.FirstPrecinctX + index % res.PrecinctsX;
        var ky = (long)res.FirstPrecinctY + index / res.PrecinctsX;
        var bands = new JpxPrecinctBand?[res.Bands.Length];
        for (var b = 0; b < bands.Length; b++)
        {
            var band = res.Bands[b];
            var x0 = Math.Max(band.X0, kx << band.PrecinctWidth);
            var y0 = Math.Max(band.Y0, ky << band.PrecinctHeight);
            var x1 = Math.Min(band.X1, (kx + 1) << band.PrecinctWidth);
            var y1 = Math.Min(band.Y1, (ky + 1) << band.PrecinctHeight);
            if (x1 <= x0 || y1 <= y0) continue;
            var bx0 = x0 >> band.BlockWidth;
            var by0 = y0 >> band.BlockHeight;
            var bw = (int)(CeilDiv(x1, 1L << band.BlockWidth) - bx0);
            var bh = (int)(CeilDiv(y1, 1L << band.BlockHeight) - by0);
            _blocks += bw * bh;
            if (_blocks > MaxBlocks || !_state.CountBlocks(bw * bh)) throw new JpxException("JPX: zu viele Code-Blöcke");
            var blocks = new JpxCodeBlock[bw * bh];
            for (var j = 0; j < bh; j++)
            for (var i = 0; i < bw; i++)
            {
                var cx = bx0 + i;
                var cy = by0 + j;
                blocks[j * bw + i] = new JpxCodeBlock
                {
                    X0 = (int)Math.Max(x0, cx << band.BlockWidth),
                    Y0 = (int)Math.Max(y0, cy << band.BlockHeight),
                    X1 = (int)Math.Min(x1, (cx + 1) << band.BlockWidth),
                    Y1 = (int)Math.Min(y1, (cy + 1) << band.BlockHeight),
                };
            }
            bands[b] = new JpxPrecinctBand(bw, bh, blocks);
        }
        return bands;
    }

    // ------------------------------------------------------------ Order of the packets (B.12)

    private IEnumerable<(int Layer, int Component, int Resolution, int Precinct)> Packets()
    {
        var maxResolutions = _components.Max(c => c.Coding.Levels) + 1;
        var progressions = _data.Header.Progressions ?? _cs.Main.Progressions
            ?? [new JpxProgression(0, 0, _layers, maxResolutions, _components.Length, _order)];
        foreach (var prog in progressions)
        {
            var lye = Math.Min(prog.LayerEnd, _layers);
            var re = Math.Min(prog.ResolutionEnd, maxResolutions);
            var ce = Math.Min(prog.ComponentEnd, _components.Length);
            var rs = prog.ResolutionStart;
            var cs = prog.ComponentStart;
            if (lye <= 0 || re <= rs || ce <= cs) continue;
            switch (prog.Order)
            {
                case 0:   // LRCP
                case 1:   // RLCP
                    // Only walk resolutions that have precincts and count every step: otherwise components without precincts and many
                    // layers produce billions of empty iterations (a crafted file: more than 20 s, 14.7 GB allocated)
                    var withPrecincts = new List<(int C, JpxResolution Res)>[re - rs];
                    for (var r = rs; r < re; r++)
                    {
                        withPrecincts[r - rs] = [];
                        for (var c = cs; c < ce; c++)
                            if (r <= _components[c].Coding.Levels && _components[c].Resolutions[r].PrecinctCount > 0)
                                withPrecincts[r - rs].Add((c, _components[c].Resolutions[r]));
                    }
                    for (var outer = 0; outer < (prog.Order == 0 ? lye : re - rs); outer++)
                    for (var inner = 0; inner < (prog.Order == 0 ? re - rs : lye); inner++)
                    {
                        var (l, r) = prog.Order == 0 ? (outer, rs + inner) : (inner, rs + outer);
                        Visit();
                        foreach (var (c, res) in withPrecincts[r - rs])
                        for (var p = 0; p < res.PrecinctCount; p++)
                        {
                            Visit();
                            if (res.NextLayer[p] != l) continue;
                            res.NextLayer[p]++;
                            yield return (l, c, r, p);
                        }
                    }
                    break;
                default:  // RPCL, PCRL, CPRL: by position of the precincts on the reference grid
                    foreach (var (c, r, p) in ByPosition(prog.Order, rs, re, cs, ce))
                    {
                        var res = _components[c].Resolutions[r];
                        for (var l = res.NextLayer[p]; l < lye; l++)
                        {
                            res.NextLayer[p]++;
                            yield return (l, c, r, p);
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>One step of the packet loop; too many (crafted file): abort, and what was read is drawn.</summary>
    private void Visit()
    {
        if (++_visits > MaxPacketSteps) throw new JpxException("JPX: zu viele Paketschritte");
    }

    private long _visits;

    /// <summary>
    /// Precincts in the order in which the loop over the reference grid (B.12.1.3–5) meets them: at their upper left
    /// corner, the first partial one at the tile edge. Sorted instead of walking every grid position.
    /// </summary>
    private List<(int C, int R, int P)> ByPosition(int order, int rs, int re, int cs, int ce)
    {
        var list = new List<(long Y, long X, int C, int R, int P)>();
        for (var c = cs; c < ce; c++)
        {
            var tc = _components[c];
            var comp = _cs.Components[c];
            for (var r = rs; r < Math.Min(re, tc.Coding.Levels + 1); r++)
            {
                var res = tc.Resolutions[r];
                var shift = tc.Coding.Levels - r;
                for (var p = 0; p < res.PrecinctCount; p++)
                {
                    var kx = (long)res.FirstPrecinctX + p % res.PrecinctsX;
                    var ky = (long)res.FirstPrecinctY + p / res.PrecinctsX;
                    var sx = kx << res.PrecinctWidth;
                    var sy = ky << res.PrecinctHeight;
                    var x = sx < res.X0 ? _tx0 : sx * comp.Dx << shift;
                    var y = sy < res.Y0 ? _ty0 : sy * comp.Dy << shift;
                    list.Add((y, x, c, r, p));
                }
            }
        }
        Comparison<(long Y, long X, int C, int R, int P)> compare = order switch
        {
            2 => (a, b) => a.R != b.R ? a.R.CompareTo(b.R) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X != b.X ? a.X.CompareTo(b.X) : a.C.CompareTo(b.C),
            3 => (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X != b.X ? a.X.CompareTo(b.X) : a.C != b.C ? a.C.CompareTo(b.C) : a.R.CompareTo(b.R),
            _ => (a, b) => a.C != b.C ? a.C.CompareTo(b.C) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X != b.X ? a.X.CompareTo(b.X) : a.R.CompareTo(b.R),
        };
        list.Sort(compare);
        return list.ConvertAll(e => (e.C, e.R, e.P));
    }

    // ------------------------------------------------------------ Blocks

    private void DecodeBlocks()
    {
        var jobs = new List<(JpxTileComponent Tc, JpxResolution Res, JpxBand Band, JpxCodeBlock Block)>();
        foreach (var tc in _components)
        {
            for (var r = 0; r <= tc.Keep; r++)
            {
                var res = tc.Resolutions[r];
                PlaceBands(tc, r);
                foreach (var precinct in res.Precincts)
                {
                    if (precinct is null) continue;
                    for (var b = 0; b < precinct.Length; b++)
                    {
                        if (precinct[b] is not { } pb) continue;
                        foreach (var block in pb.Blocks)
                            if (block.Segments is { Count: > 0 }) jobs.Add((tc, res, res.Bands[b], block));
                    }
                }
            }
        }
        if (jobs.Count == 0) return;

        // Blocks are independent: distributed over the cores, one decoder per thread
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, jobs.Count, Math.Max(1, jobs.Count / (Environment.ProcessorCount * 4))),
            () => new JpxBlockDecoder(),
            (range, loop, decoder) =>
            {
                for (var j = range.Item1; j < range.Item2 && !_state.OverBudget; j++)
                {
                    var (tc, _, band, block) = jobs[j];
                    try
                    {
                        DecodeBlock(decoder, tc, band, block);
                    }
                    catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException)
                    {
                        // A damaged block stays empty; the rest is drawn
                        _state.Truncated = true;
                    }
                }
                return decoder;
            },
            _ => { });
    }

    /// <summary>Position of the sub-bands of a resolution in the buffer (quadrants: low-pass left/top, high-pass right/bottom).</summary>
    private static void PlaceBands(JpxTileComponent tc, int r)
    {
        if (r == 0) return;
        var lower = tc.Resolutions[r - 1];
        foreach (var band in tc.Resolutions[r].Bands)
        {
            band.OffsetX = (band.Orientation & 1) != 0 ? lower.Width : 0;
            band.OffsetY = (band.Orientation & 2) != 0 ? lower.Height : 0;
        }
    }

    private void DecodeBlock(JpxBlockDecoder decoder, JpxTileComponent tc, JpxBand band, JpxCodeBlock block)
    {
        var top = band.MagnitudeBits - 1 - block.ZeroPlanes + tc.Roi;
        if (top < 0) return;
        if (top > 29)
        {
            _state.Truncated = true;
            return;
        }
        var segments = new List<(byte[] Data, int Passes)>(block.Segments!.Count);
        foreach (var s in block.Segments!) segments.Add((s.Join(), s.Passes));
        int w = block.X1 - block.X0, h = block.Y1 - block.Y0;
        var work = decoder.Decode(w, h, segments, band.Orientation, tc.Coding.Style, top);
        _state.AddWork(work);

        var stride = tc.Resolutions[tc.Keep].Width;
        var data = tc.Data;
        var roi = tc.Roi;
        var reversible = tc.Coding.Reversible;
        var half = band.Step * 0.5f;
        var baseX = block.X0 - band.X0 + band.OffsetX;
        var baseY = block.Y0 - band.Y0 + band.OffsetY;
        for (var y = 0; y < h; y++)
        {
            var row = (baseY + y) * stride + baseX;
            for (var x = 0; x < w; x++)
            {
                var v = decoder.At(x, y);
                if (v == 0) continue;
                var m = Math.Abs(v);
                // ROI (Maxshift): the coefficients of the region were scaled up by 2^roi
                if (roi > 0 && m >> 1 >= 1 << roi) m >>= roi;
                float value = reversible ? m >> 1 : m * half;
                data[row + x] = v < 0 ? -value : value;
            }
        }
    }
}

/// <summary>Shared state of an image across all tiles: limits for memory and work, abort.</summary>
internal sealed class JpxDecodeState(long workBudget, long memoryBudget, long blockBudget)
{
    private long _work;
    private long _memory;
    private long _blocks;

    public bool Truncated;

    public bool OverBudget => Interlocked.Read(ref _work) > workBudget;

    public void AddWork(long work)
    {
        if (Interlocked.Add(ref _work, work) > workBudget) Truncated = true;
    }

    public void Reserve(long bytes)
    {
        _memory += bytes;
        if (_memory > memoryBudget) throw new JpxException("JPX: zu groß");
    }

    public void Release(long bytes) => _memory -= bytes;

    public bool CountBlocks(int count) => (_blocks += count) <= blockBudget;
}
