namespace PdfKnead.Core.Pdf.Filters.Jpx;

/// <summary>Damaged or unsupported JPEG 2000 codestream.</summary>
internal sealed class JpxException(string message) : PdfException(message);

/// <summary>One image component according to SIZ: bit depth, signedness, subsampling.</summary>
internal readonly record struct JpxComponent(int Precision, bool Signed, int Dx, int Dy);

/// <summary>Coding of a component (COD/COC, ISO/IEC 15444-1, A.6.1/A.6.2).</summary>
internal sealed class JpxCoding
{
    /// <summary>Number of decomposition levels (NL).</summary>
    public int Levels;

    /// <summary>Code-block size as a power of two.</summary>
    public int BlockWidth, BlockHeight;

    /// <summary>Code-block style (bypass, context reset, termination on each pass, vertically causal, segmentation symbols).</summary>
    public int Style;

    /// <summary>5/3 (reversible, lossless) instead of 9/7.</summary>
    public bool Reversible;

    /// <summary>Per resolution: PPx (lower 4 bits) and PPy (upper 4 bits); 15/15 if not given.</summary>
    public byte[] Precincts = [];

    public int PrecinctWidth(int r) => r < Precincts.Length ? Precincts[r] & 15 : 15;

    public int PrecinctHeight(int r) => r < Precincts.Length ? Precincts[r] >> 4 : 15;
}

/// <summary>Quantisation of a component (QCD/QCC, ISO/IEC 15444-1, A.6.4/A.6.5).</summary>
internal sealed class JpxQuantization
{
    /// <summary>0 none, 1 derived (only LL given), 2 expounded (per sub-band).</summary>
    public int Style;

    public int GuardBits;
    public int[] Exponents = [];
    public int[] Mantissas = [];

    /// <summary>Exponent and mantissa of the sub-band with QCD index <paramref name="band"/> (LL = 0, then HL, LH, HH per level).</summary>
    public (int Exponent, int Mantissa) Band(int band, int levels, int decompositionLevel)
    {
        if (Style == 1)
        {
            // Derived (ISO/IEC 15444-1, equation E-5): from the LL value, one exponent less per level
            var e0 = Exponents.Length > 0 ? Exponents[0] : 0;
            var m0 = Mantissas.Length > 0 ? Mantissas[0] : 0;
            return (Math.Max(0, e0 - levels + decompositionLevel), m0);
        }
        if (Exponents.Length == 0) return (0, 0);
        var i = Math.Min(band, Exponents.Length - 1);
        return (Exponents[i], Mantissas[i]);
    }
}

/// <summary>One entry of POC (progression order change, ISO/IEC 15444-1, A.6.6).</summary>
internal readonly record struct JpxProgression(int ResolutionStart, int ComponentStart, int LayerEnd, int ResolutionEnd, int ComponentEnd, int Order);

/// <summary>Values from the main header or a tile header; whatever is missing comes from the main header.</summary>
internal sealed class JpxHeaderValues
{
    public JpxCoding? Coding;
    public JpxCoding?[] ComponentCoding = [];
    public JpxQuantization? Quantization;
    public JpxQuantization?[] ComponentQuantization = [];
    public int?[] RoiShift = [];
    public List<JpxProgression>? Progressions;

    // From COD (Scod/SGcod): apply to the whole tile
    public bool HasCod;
    public int Order;
    public int Layers = 1;

    /// <summary>The component transformation of ISO/IEC 15444-1 (RCT or ICT on components 0, 1, 2).</summary>
    public bool MultipleComponentTransform;

    /// <summary>The multiple component transformation field of SGcod as written (ISO/IEC 15444-2, Table A.8: 2 array-based, 4 wavelet-based).</summary>
    public int ComponentTransform;

    /// <summary>The marker segments of ISO/IEC 15444-2 in this header (MCT, MCC, MCO, NLT).</summary>
    public readonly JpxPart2Header Part2 = new();

    public void Init(int components)
    {
        ComponentCoding = new JpxCoding?[components];
        ComponentQuantization = new JpxQuantization?[components];
        RoiShift = new int?[components];
    }
}

/// <summary>What a tile needs from its tile-parts.</summary>
internal sealed class JpxTileData
{
    public readonly JpxHeaderValues Header = new();
    public readonly List<(int Start, int End)> Bodies = [];
    public readonly List<(int Index, byte[] Data)> Ppt = [];
    public readonly List<byte[]> PpmChunks = [];
    public int Parts;
}

/// <summary>
/// JPEG 2000 codestream (ISO/IEC 15444-1, Annex A): main header and tile-parts. The data of the tile-parts is only
/// recorded (start, end); it is decoded only when the tile is decoded.
/// </summary>
internal sealed class JpxCodestream
{
    /// <summary>At most this many components (as in the standard); the decoder limits the memory.</summary>
    private const int MaxComponents = 16384;

    public readonly byte[] Data;
    public int X0, Y0, X1, Y1;
    public int TileWidth, TileHeight, TileX0, TileY0;
    public int TilesX, TilesY;
    public JpxComponent[] Components = [];

    /// <summary>Rsiz of SIZ: the capabilities a decoder needs (ISO/IEC 15444-1, Table A.10; ISO/IEC 15444-2, Table A.2).</summary>
    public int Rsiz;

    /// <summary>
    /// The reconstructed image components (ISO/IEC 15444-2, J.2): those of CBD where the multiple component transformation is used, otherwise
    /// <see cref="Components"/>.
    /// </summary>
    public JpxComponent[] Reconstructed = [];

    /// <summary>The decoded components: <see cref="Reconstructed"/> with the bit depth and sign a non-linearity gives them (Annex K).</summary>
    public JpxComponent[] Output = [];

    public readonly JpxHeaderValues Main = new();
    public JpxTileData?[] Tiles = [];
    public bool Truncated;

    private JpxCodestream(byte[] data) => Data = data;

    public int Width => X1 - X0;
    public int Height => Y1 - Y0;

    public static JpxCodestream Parse(byte[] data, int start, int end)
    {
        var cs = new JpxCodestream(data);
        cs.Read(start, end);
        return cs;
    }

    private int U8(int i) => Data[i];
    private int U16(int i) => Data[i] << 8 | Data[i + 1];
    private long U32(int i) => (long)Data[i] << 24 | (long)Data[i + 1] << 16 | (long)Data[i + 2] << 8 | Data[i + 3];

    private void Read(int pos, int end)
    {
        if (end - pos < 4 || U16(pos) != 0xFF4F) throw new JpxException("JPX: kein Datenstrom");
        pos += 2;
        var ppm = new List<(int Index, byte[] Data)>();
        var sawSiz = false;
        JpxComponentDepth[]? cbd = null;

        // Main header up to the first tile-part
        while (true)
        {
            if (pos + 4 > end) throw new JpxException("JPX: Kopf unvollständig");
            var marker = U16(pos);
            if (marker == 0xFF90) break;
            if (marker is >= 0xFF30 and <= 0xFF3F)
            {
                // Reserved markers without a length: skip (ISO/IEC 15444-1, A.1.4)
                pos += 2;
                continue;
            }
            if (marker == 0xFFD9) throw new JpxException("JPX: keine Kacheln");
            var length = U16(pos + 2);
            if (length < 2 || pos + 2 + length > end) throw new JpxException("JPX: Markerlänge");
            var s = pos + 4;
            var e = pos + 2 + length;
            if (marker == 0xFF51)
            {
                ReadSiz(s, e);
                sawSiz = true;
            }
            else if (!sawSiz && marker is >= 0xFF52 and <= 0xFF5F)
            {
                throw new JpxException("JPX: SIZ fehlt");
            }
            else if (marker == 0xFF60)
            {
                if (e > s) ppm.Add((U8(s), Data[(s + 1)..e]));
            }
            else if (marker == 0xFF78)
            {
                cbd = JpxPart2.ReadCbd(Data, s, e);   // CBD: main header only
            }
            else
            {
                ReadCommon(Main, marker, s, e);
            }
            pos = e;
        }
        if (!sawSiz) throw new JpxException("JPX: SIZ fehlt");
        if (Main.Coding is null || Main.Quantization is null) throw new JpxException("JPX: COD/QCD fehlt");
        SetOutput(cbd);

        // PPM: packet headers of all tile-parts, each tile-part preceded by Nppm (4 bytes)
        Queue<byte[]>? ppmChunks = null;
        if (ppm.Count > 0)
        {
            var all = ppm.OrderBy(p => p.Index).SelectMany(p => p.Data).ToArray();
            ppmChunks = new Queue<byte[]>();
            for (var i = 0; i + 4 <= all.Length;)
            {
                var n = (int)Math.Min(all.Length - i - 4, (long)all[i] << 24 | (long)all[i + 1] << 16 | (long)all[i + 2] << 8 | all[i + 3]);
                ppmChunks.Enqueue(all[(i + 4)..(i + 4 + n)]);
                i += 4 + n;
            }
        }

        // Tile-parts
        while (pos + 12 <= end && U16(pos) == 0xFF90)
        {
            var lsot = U16(pos + 2);
            var tile = U16(pos + 4);
            var psot = U32(pos + 6);
            if (lsot != 10 || tile >= Tiles.Length)
            {
                Truncated = true;
                break;
            }
            var data = Tiles[tile] ??= NewTile();
            var first = data.Parts == 0;
            data.Parts++;
            var partEnd = psot == 0 ? end : (int)Math.Min(end, pos + psot);
            var p = pos + 12;
            var sod = false;
            while (p + 2 <= partEnd)
            {
                var marker = U16(p);
                if (marker is >= 0xFF30 and <= 0xFF3F)
                {
                    p += 2;
                    continue;
                }
                if (marker == 0xFF93)
                {
                    p += 2;
                    sod = true;
                    break;
                }
                if (p + 4 > partEnd) break;
                var length = U16(p + 2);
                if (length < 2 || p + 2 + length > partEnd) break;
                var s = p + 4;
                var e = p + 2 + length;
                if (marker == 0xFF61)
                {
                    if (e > s) data.Ppt.Add((U8(s), Data[(s + 1)..e]));
                }
                else if (first || marker == 0xFF5F)
                {
                    // COD, COC, QCD, QCC, RGN only in the first tile-part; POC in every one
                    ReadCommon(data.Header, marker, s, e);
                }
                p = e;
            }
            if (!sod)
            {
                Truncated = true;
                break;
            }
            // Without a length (Psot 0) the last tile-part extends up to EOC
            var bodyEnd = partEnd;
            if (psot == 0 && bodyEnd - 2 >= p && U16(bodyEnd - 2) == 0xFFD9) bodyEnd -= 2;
            data.Bodies.Add((p, bodyEnd));
            if (ppmChunks is { Count: > 0 }) data.PpmChunks.Add(ppmChunks.Dequeue());
            if (psot != 0 && pos + psot > end) Truncated = true;
            pos = partEnd;
            if (pos + 2 <= end && U16(pos) == 0xFFD9) break;
        }
    }

    /// <summary>
    /// The components the decoder hands out (ISO/IEC 15444-2, J.2 and K.1.2): with the multiple component transformation the ones of CBD, which
    /// takes their number and bit depths from SIZ — all of the size of the codestream components, which therefore must share their
    /// subsampling (the inputs of a collection must, J.2.3); then the bit depth after a non-linearity of the main header.
    /// </summary>
    private void SetOutput(JpxComponentDepth[]? cbd)
    {
        Reconstructed = Components;
        if (JpxPart2.MultipleComponent(Rsiz))
        {
            if ((Main.ComponentTransform & 4) != 0) throw new JpxException("JPX: Wavelet-Komponenten-Transformation nicht unterstützt");
            if (cbd is not null)
            {
                if (Components.Any(c => c.Dx != Components[0].Dx || c.Dy != Components[0].Dy))
                    throw new JpxException("JPX: Komponenten-Transformation mit Unterabtastung");
                Reconstructed = [.. cbd.Select(d => new JpxComponent(d.Bits, d.Signed, Components[0].Dx, Components[0].Dy))];
            }
        }
        Output = [.. Reconstructed];
        if (!JpxPart2.Extended(Rsiz)) return;
        var none = new JpxPart2Header();
        for (var c = 0; c < Output.Length; c++)
            if (JpxPart2.NonLinearityOf(none, Main.Part2, c) is { } nlt) Output[c] = Output[c] with { Precision = nlt.Bits, Signed = nlt.Signed };
    }

    private JpxTileData NewTile()
    {
        var tile = new JpxTileData();
        tile.Header.Init(Components.Length);
        return tile;
    }

    private void ReadSiz(int s, int e)
    {
        if (e - s < 36) throw new JpxException("JPX: SIZ zu kurz");
        Rsiz = U16(s);
        X1 = Int(U32(s + 2));
        Y1 = Int(U32(s + 6));
        X0 = Int(U32(s + 10));
        Y0 = Int(U32(s + 14));
        TileWidth = Int(U32(s + 18));
        TileHeight = Int(U32(s + 22));
        TileX0 = Int(U32(s + 26));
        TileY0 = Int(U32(s + 30));
        var count = U16(s + 34);
        if (count is < 1 or > MaxComponents || e - s < 36 + 3 * count) throw new JpxException("JPX: Komponenten");
        if (X1 <= X0 || Y1 <= Y0 || TileWidth <= 0 || TileHeight <= 0 || TileX0 > X0 || TileY0 > Y0
            || (long)TileX0 + TileWidth <= X0 || (long)TileY0 + TileHeight <= Y0)
            throw new JpxException("JPX: Bildgröße");
        Components = new JpxComponent[count];
        for (var c = 0; c < count; c++)
        {
            var ssiz = U8(s + 36 + 3 * c);
            var dx = U8(s + 37 + 3 * c);
            var dy = U8(s + 38 + 3 * c);
            if (dx == 0 || dy == 0) throw new JpxException("JPX: Unterabtastung");
            Components[c] = new JpxComponent((ssiz & 0x7F) + 1, (ssiz & 0x80) != 0, dx, dy);
        }
        long tilesX = ((long)X1 - TileX0 + TileWidth - 1) / TileWidth;
        long tilesY = ((long)Y1 - TileY0 + TileHeight - 1) / TileHeight;
        // Isot has 16 bits, so there cannot be more tiles
        if (tilesX * tilesY > 65535) throw new JpxException("JPX: zu viele Kacheln");
        TilesX = (int)tilesX;
        TilesY = (int)tilesY;
        Tiles = new JpxTileData?[TilesX * TilesY];
        Main.Init(count);
    }

    private static int Int(long v) => v > int.MaxValue ? throw new JpxException("JPX: Koordinate zu groß") : (int)v;

    private void ReadCommon(JpxHeaderValues h, int marker, int s, int e)
    {
        var wideIndex = Components.Length >= 257;
        int Index(ref int p)
        {
            var c = wideIndex ? U16(p) : U8(p);
            p += wideIndex ? 2 : 1;
            return c;
        }

        switch (marker)
        {
            case 0xFF52:   // COD
            {
                if (e - s < 10) return;
                var scod = U8(s);
                h.HasCod = true;
                h.Order = U8(s + 1);
                h.Layers = Math.Max(1, U16(s + 2));
                // ISO/IEC 15444-1: any value means RCT/ICT; with the multiple component capability of ISO/IEC 15444-2 bit 0 alone does, bit 1
                // is the array-based and bit 2 the wavelet-based transformation (Table A.8)
                h.ComponentTransform = U8(s + 4);
                h.MultipleComponentTransform = JpxPart2.MultipleComponent(Rsiz) ? (h.ComponentTransform & 1) != 0 : h.ComponentTransform != 0;
                h.Coding = ReadCoding(scod, s + 5, e);
                return;
            }
            // ISO/IEC 15444-2: only with the capability in Rsiz; in a codestream of ISO/IEC 15444-1 these codes mean nothing
            case 0xFF74 when JpxPart2.MultipleComponent(Rsiz):   // MCT
                JpxPart2.ReadMct(h.Part2, Data, s, e);
                return;
            case 0xFF75 when JpxPart2.MultipleComponent(Rsiz):   // MCC
                JpxPart2.ReadMcc(h.Part2, Data, s, e);
                return;
            case 0xFF77 when JpxPart2.MultipleComponent(Rsiz):   // MCO
                JpxPart2.ReadMco(h.Part2, Data, s, e);
                return;
            case 0xFF76 when JpxPart2.Extended(Rsiz):            // NLT
                JpxPart2.ReadNlt(h.Part2, Data, s, e);
                return;
            case 0xFF53:   // COC
            {
                var p = s;
                if (e - p < (wideIndex ? 2 : 1) + 6) return;
                var c = Index(ref p);
                if (c >= h.ComponentCoding.Length) return;
                var scoc = U8(p);
                h.ComponentCoding[c] = ReadCoding(scoc, p + 1, e);
                return;
            }
            case 0xFF5C:   // QCD
                h.Quantization = ReadQuantization(s, e);
                return;
            case 0xFF5D:   // QCC
            {
                var p = s;
                if (e - p < (wideIndex ? 3 : 2)) return;
                var c = Index(ref p);
                if (c < h.ComponentQuantization.Length) h.ComponentQuantization[c] = ReadQuantization(p, e);
                return;
            }
            case 0xFF5E:   // RGN
            {
                var p = s;
                if (e - p < (wideIndex ? 4 : 3)) return;
                var c = Index(ref p);
                if (c < h.RoiShift.Length && U8(p) == 0) h.RoiShift[c] = U8(p + 1);
                return;
            }
            case 0xFF5F:   // POC
            {
                var p = s;
                var entry = wideIndex ? 9 : 7;
                h.Progressions ??= [];
                while (e - p >= entry && h.Progressions.Count < 1024)
                {
                    var rs = U8(p++);
                    var cs = Index(ref p);
                    var lye = U16(p);
                    p += 2;
                    var re = U8(p++);
                    var ce = Index(ref p);
                    var order = U8(p++);
                    h.Progressions.Add(new JpxProgression(rs, cs, lye, re, ce == 0 && !wideIndex ? 256 : ce, order));
                }
                return;
            }
        }
    }

    private JpxCoding ReadCoding(int style, int p, int e)
    {
        if (e - p < 5) throw new JpxException("JPX: COD zu kurz");
        var coding = new JpxCoding
        {
            Levels = U8(p),
            BlockWidth = U8(p + 1) + 2,
            BlockHeight = U8(p + 2) + 2,
            Style = U8(p + 3),
            Reversible = U8(p + 4) == 1,
        };
        if (coding.Levels > 32 || coding.BlockWidth > 10 || coding.BlockHeight > 10 || coding.BlockWidth + coding.BlockHeight > 12)
            throw new JpxException("JPX: Codierung");
        if ((style & 1) != 0)
        {
            var count = coding.Levels + 1;
            if (e - p - 5 < count) throw new JpxException("JPX: Bereiche");
            coding.Precincts = Data[(p + 5)..(p + 5 + count)];
        }
        return coding;
    }

    private JpxQuantization ReadQuantization(int s, int e)
    {
        if (e - s < 1) throw new JpxException("JPX: QCD zu kurz");
        var sq = U8(s);
        var q = new JpxQuantization { Style = sq & 0x1F, GuardBits = sq >> 5 };
        var p = s + 1;
        if (q.Style == 0)
        {
            var n = e - p;
            q.Exponents = new int[n];
            q.Mantissas = new int[n];
            for (var i = 0; i < n; i++) q.Exponents[i] = U8(p + i) >> 3;
        }
        else
        {
            var n = (e - p) / 2;
            if (n == 0) throw new JpxException("JPX: QCD zu kurz");
            q.Exponents = new int[n];
            q.Mantissas = new int[n];
            for (var i = 0; i < n; i++)
            {
                var v = U16(p + 2 * i);
                q.Exponents[i] = v >> 11;
                q.Mantissas[i] = v & 0x7FF;
            }
        }
        return q;
    }
}
