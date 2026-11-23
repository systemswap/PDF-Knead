namespace PdfKnead.Core.Pdf.Filters.Jpx;

/// <summary>What <see cref="JpxProfile.Check"/> finds in a codestream.</summary>
internal enum JpxProfileResult
{
    /// <summary>Rsiz names neither profile 0 nor profile 1 (it is not 1 or 2).</summary>
    NotNamed,

    /// <summary>The codestream cannot be walked from SIZ to EOC, and no restriction was found broken before the point where it stopped.</summary>
    Unreadable,

    /// <summary>Rsiz names profile 0 or 1, and the codestream breaks a restriction Table A.45 sets for it.</summary>
    Broken,

    /// <summary>Profile 0 named and kept.</summary>
    Profile0,

    /// <summary>Profile 1 named and kept.</summary>
    Profile1,
}

/// <summary>
/// The restrictions of the Part 1 profiles 0 and 1 (ITU-T T.800, Table A.45) on a codestream whose Rsiz names one of them (1 or 2, Table A.10):
/// image size, tiles, image and tile origin, subsampling, RGN shift, code-block size and style, packed headers, coding and quantization segments
/// in the main header only, the first progression of a POC, the order of the tile-parts, the size of the lowest resolution and the precincts.
/// Rsiz promises the restrictions without keeping them. The writer frames a codestream as profile 0 or 1 only where it keeps them
/// (<c>PdfAJpeg2000.Wrap</c>; docs/FEHLERGEDAECHTNIS.md, No. 1074); the decoder reads a codestream that breaks them as before and reports it
/// (decided on 23.11.2026: read and report; No. 1077). The tile size of profile 1 is at most 1024 per subsampling step: the conformance
/// codestreams of T.803 for profile 1 have tiles of 3, 37 and 128 grid points.
/// <para>
/// Only the marker segments are read: the main header, and each tile-part header up to SOD, from where Psot leads on to the next tile-part —
/// the packet data is not looked at, so the cost grows with the number of tile-parts and header segments, not with the size of the image.
/// </para>
/// </summary>
internal static class JpxProfile
{
    /// <summary>
    /// Checks a codestream (from SOC; it may end at EOC). A restriction found broken gives <see cref="JpxProfileResult.Broken"/> even where the
    /// codestream cannot be walked further; a codestream that cannot be walked from SIZ to EOC before that gives
    /// <see cref="JpxProfileResult.Unreadable"/> — the restrictions that need every tile-part (the order of the tile-parts, the lowest
    /// resolution and the precincts of every tile) are checked only on a complete walk.
    /// </summary>
    /// <param name="cs">The codestream.</param>
    /// <returns>What it finds.</returns>
    public static JpxProfileResult Check(ReadOnlySpan<byte> cs)
    {
        static uint U32(ReadOnlySpan<byte> d, int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d[i..]);
        static int U16(ReadOnlySpan<byte> d, int i) => d[i] << 8 | d[i + 1];
        static long CeilDiv(long a, long b) => (a + b - 1) / b;
        const JpxProfileResult unreadable = JpxProfileResult.Unreadable, broken = JpxProfileResult.Broken;
        if (cs.Length < 42 || U16(cs, 0) != 0xFF4F || U16(cs, 2) != 0xFF51) return unreadable;
        var rsiz = U16(cs, 6);
        if (rsiz is not (1 or 2)) return JpxProfileResult.NotNamed;
        var check = new ProfileCheck(rsiz == 1, U16(cs, 40));
        var n = check.Components;
        var lsiz = U16(cs, 4);
        if (n == 0 || lsiz != 38 + 3 * n || cs.Length < 4 + lsiz) return unreadable;
        long xsiz = U32(cs, 8), ysiz = U32(cs, 12), xo = U32(cs, 16), yo = U32(cs, 20);
        long xt = U32(cs, 24), yt = U32(cs, 28), xto = U32(cs, 32), yto = U32(cs, 36);
        // the relations of the reference grid (T.800 A.5.1, B.3): without them it is no codestream of T.800 at all
        if (xo >= xsiz || yo >= ysiz || xt == 0 || yt == 0 || xto > xo || yto > yo || xto + xt <= xo || yto + yt <= yo) return unreadable;
        var dx = new int[n];
        var dy = new int[n];
        for (var c = 0; c < n; c++)
        {
            (dx[c], dy[c]) = (cs[43 + 3 * c], cs[44 + 3 * c]);
            if (dx[c] == 0 || dy[c] == 0) return unreadable;
        }
        // image size below 2^31 (both profiles); the origins of profile 1 lie below it then too, as XTOsiz ≤ XOsiz < Xsiz
        if (xsiz >= 1L << 31 || ysiz >= 1L << 31) return broken;
        var whole = xt + xto >= xsiz && yt + yto >= ysiz;   // one tile for the whole image
        if (check.P0)
        {
            if (xo != 0 || yo != 0 || xto != 0 || yto != 0) return broken;
            if (!(xt == 128 && yt == 128) && !whole) return broken;
            for (var c = 0; c < n; c++)
                if (dx[c] is not (1 or 2 or 4) || dy[c] is not (1 or 2 or 4)) return broken;
        }
        else if (!whole)
        {
            if (xt != yt) return broken;
            for (var c = 0; c < n; c++)
                if (xt > 1024L * Math.Min(dx[c], dy[c])) return broken;
        }
        long tilesX = CeilDiv(xsiz - xto, xt), tilesY = CeilDiv(ysiz - yto, yt);
        if (tilesX * tilesY > 65_535) return unreadable;   // Isot numbers at most 65 535 tiles (Table A.5)
        var tiles = (int)(tilesX * tilesY);

        // the main header up to the first SOT
        var pos = 4 + lsiz;
        while (true)
        {
            if (pos + 2 > cs.Length) return unreadable;
            var code = U16(cs, pos);
            if (code == 0xFF90) break;
            if (code is >= 0xFF30 and <= 0xFF3F)
            {
                pos += 2;   // markers without a segment (T.800 A.1): passed over
                continue;
            }
            var length = pos + 4 <= cs.Length ? U16(cs, pos + 2) : 0;
            if (length < 2 || pos + 2 + length > cs.Length) return unreadable;
            if (check.Segment(cs, code, pos + 4, pos + 2 + length, -1) is { } fault) return fault;
            pos += 2 + length;
        }
        if (check.Cod is null || !check.Qcd) return unreadable;
        // the tile-parts, each header up to SOD, then on by Psot to the next SOT or to EOC
        var order = new List<(int Tile, int Part)>();
        while (true)
        {
            if (pos + 2 > cs.Length) return unreadable;
            if (U16(cs, pos) == 0xFFD9) break;
            if (U16(cs, pos) != 0xFF90 || pos + 12 > cs.Length || U16(cs, pos + 2) != 10) return unreadable;
            int tile = U16(cs, pos + 4), part = cs[pos + 10];
            long psot = U32(cs, pos + 6);
            if (tile >= tiles) return unreadable;
            order.Add((tile, part));
            var at = pos + 12;
            while (true)
            {
                if (at + 2 > cs.Length) return unreadable;
                var code = U16(cs, at);
                if (code == 0xFF93) break;
                if (code is >= 0xFF30 and <= 0xFF3F)
                {
                    at += 2;
                    continue;
                }
                var length = at + 4 <= cs.Length ? U16(cs, at + 2) : 0;
                if (length < 2 || at + 2 + length > cs.Length) return unreadable;
                if (check.Segment(cs, code, at + 4, at + 2 + length, tile) is { } fault) return fault;
                at += 2 + length;
            }
            if (psot == 0)
            {
                // the last tile-part runs to EOC
                if (cs.Length < at + 4 || U16(cs, cs.Length - 2) != 0xFFD9) return unreadable;
                break;
            }
            if (at + 2 > pos + psot || pos + psot > cs.Length) return unreadable;
            pos += (int)psot;
        }

        if (check.P0)
        {
            // every tile's first tile-part (TPsot 0) before all others, the tiles in order 0 to the last
            var firsts = 0;
            var later = false;
            foreach (var (tile, part) in order)
            {
                if (part != 0) later = true;
                else if (later || tile != firsts++) return broken;
            }
            if (firsts != tiles) return broken;
            // the lowest resolution of components 0 to 3 at most 128 × 128 where one tile holds the image
            if (whole)
                for (var c = 0; c < Math.Min(4, n); c++)
                {
                    var levels = check.Of(-1, c).Levels;
                    if (xsiz - xo > 128L << levels || ysiz - yo > 128L << levels) return broken;
                }
            // one precinct in every resolution of at most 128 × 128. With the origins at 0, tiles of 128 (or one tile) and subsampling by 1, 2
            // or 4, every column of tiles but the last has the extents and precincts of the first, and the last is no larger: the first and
            // the last column and row decide for all
            long[] columns = [0, tilesX - 1], rows = [0, tilesY - 1];
            for (var c = 0; c < n; c++)
            {
                var coding = check.Of(-1, c);
                for (var r = 0; r <= coding.Levels; r++)
                {
                    var (ppx, ppy) = coding.Precinct(r);
                    foreach (var column in columns)
                    {
                        var (width, across) = Extent(column, xto, xt, xo, xsiz, dx[c], coding.Levels - r, ppx);
                        foreach (var row in rows)
                        {
                            var (height, down) = Extent(row, yto, yt, yo, ysiz, dy[c], coding.Levels - r, ppy);
                            if (width <= 128 && height <= 128 && across * down > 1) return broken;
                        }
                    }
                }
            }
            return JpxProfileResult.Profile0;
        }

        // profile 1: the lowest resolution of components 0 to 3 at most 128 × 128 in every tile, ⌊t1 / D⌋ − ⌊t0 / D⌋ with the tile on the
        // reference grid (B-7 to B-10) and D = 2^levels of the tile and component
        for (var t = 0; t < tiles; t++)
        {
            long p = t % tilesX, q = t / tilesX;
            long tx0 = Math.Max(xto + p * xt, xo), tx1 = Math.Min(xto + (p + 1) * xt, xsiz);
            long ty0 = Math.Max(yto + q * yt, yo), ty1 = Math.Min(yto + (q + 1) * yt, ysiz);
            for (var c = 0; c < Math.Min(4, n); c++)
            {
                var levels = check.Of(t, c).Levels;
                if ((tx1 >> levels) - (tx0 >> levels) > 128 || (ty1 >> levels) - (ty0 >> levels) > 128) return broken;
            }
        }
        return JpxProfileResult.Profile1;

        // the extent of one column (or row) of tiles in a resolution k levels below the full one, and its number of precincts (B-7 to
        // B-16): 0 where the resolution is empty
        static (long Size, long Precincts) Extent(long index, long tileOrigin, long tileSize, long origin, long size, int factor, int k, int pp)
        {
            long t0 = Math.Max(tileOrigin + index * tileSize, origin), t1 = Math.Min(tileOrigin + (index + 1) * tileSize, size);
            long r0 = CeilDiv(CeilDiv(t0, factor), 1L << k), r1 = CeilDiv(CeilDiv(t1, factor), 1L << k);
            return (r1 - r0, r1 > r0 ? CeilDiv(r1, 1L << pp) - (r0 >> pp) : 0);
        }
    }

    /// <summary>The coding style of a tile-component as COD or COC give it (T.800 A.6.1, A.6.2): levels, code-block size and style, precincts.</summary>
    private sealed record Coding(int Levels, int BlockWidth, int BlockHeight, int Style, byte[] Precincts)
    {
        /// <summary>PPx and PPy of resolution <paramref name="r"/>: as given, else 15 (Table A.13).</summary>
        public (int X, int Y) Precinct(int r) => Precincts.Length > r ? (Precincts[r] & 15, Precincts[r] >> 4) : (15, 15);
    }

    /// <summary>What <see cref="Check"/> reads from the marker segments, and the restrictions of Table A.45 they break.</summary>
    private sealed class ProfileCheck(bool p0, int components)
    {
        public bool P0 { get; } = p0;
        public int Components { get; } = components;
        public Coding? Cod { get; private set; }
        public bool Qcd { get; private set; }
        private readonly Dictionary<int, Coding> _coc = [];
        private readonly Dictionary<int, Coding> _tileCod = [];
        private readonly Dictionary<(int Tile, int Component), Coding> _tileCoc = [];

        /// <summary>The coding style of component <paramref name="c"/> in tile <paramref name="tile"/> (-1: the main header): COC of the tile,
        /// COD of the tile, COC of the main header, COD of the main header (A.6.1).</summary>
        public Coding Of(int tile, int c) =>
            _tileCoc.GetValueOrDefault((tile, c)) ?? _tileCod.GetValueOrDefault(tile) ?? _coc.GetValueOrDefault(c) ?? Cod!;

        /// <summary>One marker segment of the main header (<paramref name="tile"/> -1) or of a tile-part header: null where it is in order,
        /// <see cref="JpxProfileResult.Broken"/> where it breaks a restriction, <see cref="JpxProfileResult.Unreadable"/> where it is
        /// damaged.</summary>
        public JpxProfileResult? Segment(ReadOnlySpan<byte> d, int code, int s, int e, int tile)
        {
            const JpxProfileResult unreadable = JpxProfileResult.Unreadable, broken = JpxProfileResult.Broken;
            var wide = Components >= 257;   // component indices of two bytes (Tables A.21, A.24, A.26, A.32)
            switch (code)
            {
                case 0xFF52 or 0xFF53 or 0xFF5C or 0xFF5D when P0 && tile >= 0:
                    return broken;   // profile 0: COD, COC, QCD and QCC in the main header only
                case 0xFF52:
                {
                    if (e - s < 10) return unreadable;
                    var (cod, fault) = Read(d, s + 5, e, (d[s] & 1) != 0);
                    if (cod is null) return fault;
                    if (tile < 0) Cod = cod;
                    else _tileCod[tile] = cod;
                    return null;
                }
                case 0xFF53:
                {
                    var at = s + (wide ? 2 : 1);
                    if (at + 6 > e) return unreadable;
                    var c = wide ? d[s] << 8 | d[s + 1] : d[s];
                    if (c >= Components) return unreadable;
                    var (coc, fault) = Read(d, at + 1, e, (d[at] & 1) != 0);
                    if (coc is null) return fault;
                    if (tile < 0) _coc[c] = coc;
                    else _tileCoc[(tile, c)] = coc;
                    return null;
                }
                case 0xFF5C:
                    if (tile < 0) Qcd = true;
                    return null;
                case 0xFF5E:
                {
                    // RGN: SPrgn at most 37 (both profiles)
                    var at = s + (wide ? 2 : 1);
                    if (at + 2 > e) return unreadable;
                    return d[at + 1] <= 37 ? null : broken;
                }
                case 0xFF5F:
                    // POC: profile 0 asks RSpoc 0 and CSpoc 0 of the first progression
                    if (e - s < (wide ? 9 : 7)) return unreadable;
                    return !P0 || d[s] == 0 && (wide ? d[s + 1] << 8 | d[s + 2] : d[s + 1]) == 0 ? null : broken;
                case 0xFF60 or 0xFF61:
                    return P0 ? broken : null;   // PPM, PPT: not in profile 0
                default:
                    return null;
            }
        }

        /// <summary>SPcod or SPcoc from <paramref name="i"/> (Tables A.15, A.21), with the code-block restrictions of the profile; no coding
        /// where it is damaged (<see cref="JpxProfileResult.Unreadable"/>) or breaks them (<see cref="JpxProfileResult.Broken"/>).</summary>
        private (Coding? Coding, JpxProfileResult Fault) Read(ReadOnlySpan<byte> d, int i, int e, bool precincts)
        {
            if (i + 5 > e) return (null, JpxProfileResult.Unreadable);
            int levels = d[i], width = d[i + 1], height = d[i + 2], style = d[i + 3];
            // Table A.18: exponents 2 to 10, together at most 12 (the values are the exponents less 2)
            if (levels > 32 || width > 8 || height > 8 || width + height > 8) return (null, JpxProfileResult.Unreadable);
            if (precincts && i + 6 + levels > e) return (null, JpxProfileResult.Unreadable);
            var coding = new Coding(levels, width + 2, height + 2, style, precincts ? d.Slice(i + 5, levels + 1).ToArray() : []);
            // profile 0: blocks of 32 × 32 or 64 × 64, only the styles termination on each pass, predictable termination and segmentation
            // symbols; profile 1: blocks of at most 64 × 64
            var kept = P0
                ? coding.BlockWidth == coding.BlockHeight && coding.BlockWidth is 5 or 6 && (style & ~0x34) == 0
                : coding.BlockWidth <= 6 && coding.BlockHeight <= 6;
            return kept ? (coding, default) : (null, JpxProfileResult.Broken);
        }
    }
}
