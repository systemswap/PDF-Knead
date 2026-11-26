using System.Collections.Concurrent;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Compact Font Format font (Adobe TN 5176) with Type 2 charstrings (Adobe TN 5177), either stand-alone
/// (PDF FontFile3 /Type1C, /CIDFontType0C) or as the "CFF " table of an OpenType font. Reads the
/// name, charset, encoding, CID mapping and the glyph outlines. The "CFF2" table of an OpenType font (OpenType 8) is read as well
/// (<see cref="ParseCff2"/>), in its default instance.
/// </summary>
/// <remarks>
/// Glyph outlines are built on first use and cached up to 64 MB per font; the members are safe to call from several threads.
/// </remarks>
public sealed class CffFont
{
    private const int MaxSubrDepth = 10;
    private const int MaxStack = 48;

    /// <summary>The argument stack of CFF2 holds 513 numbers (OpenType 8.4.1), that of Type 2 charstrings 48 (Adobe TN 5177, Appendix B).</summary>
    internal const int MaxStackCff2 = 513;

    private readonly ArraySegment<byte> _data;   // the CFF data (for OpenType a slice of the file)
    private readonly CffIndex _charStrings;
    private readonly CffIndex _globalSubrs;
    private readonly PrivateDict[] _privates;

    /// <summary>The Font DICTs of a CIDFont that FDSelect can name: its values are Card8 (Adobe TN 5176, 19).</summary>
    internal const int MaxFontDicts = 256;
    private readonly ushort[]? _fdSelect;        // glyph → Font DICT (CID-keyed fonts, CFF2)
    private bool _cff2;                           // the data are CFF2 (OpenType 8)
    private int[]? _regionCounts;                 // CFF2: the regions of each ItemVariationData (k; -1 unreadable), null without VariationStore
    private double[]? _topMatrix;                 // CFF2: the operands of the Top DICT's FontMatrix, if it has one
    private Matrix?[]? _fdRelative;               // Font DICT → its own FontMatrix relative to FontMatrix (null: the same)
    private readonly string[]? _names;           // glyph → name (not CID-keyed)
    private readonly Dictionary<string, int>? _nameToGlyph;
    private readonly int[]? _glyphToCid;
    private readonly int[]? _cidToGlyph;         // CID → glyph (-1 = none) when the CIDs are dense
    private readonly int[]? _sparseCids;         // otherwise sorted CIDs …
    private readonly int[]? _sparseGlyphs;       // … with their glyph (subsets: few glyphs, high CIDs)
    /// <summary>Outlines per glyph, at most 64 MB (<see cref="BoundedCache{TKey, TValue}"/>).</summary>
    private readonly BoundedCache<int, (GlyphOutline Outline, double Width)> _cache = new(64L << 20, g => g.Outline.EstimatedBytes);

    private CffFont(ArraySegment<byte> data, string name, Matrix fontMatrix, CffIndex charStrings, CffIndex globalSubrs, PrivateDict[] privates,
        ushort[]? fdSelect, string[]? names, Dictionary<string, int>? nameToGlyph, int[]? glyphToCid, int[] encoding)
    {
        _data = data;
        Name = name;
        FontMatrix = fontMatrix;
        _charStrings = charStrings;
        _globalSubrs = globalSubrs;
        _privates = privates;
        _fdSelect = fdSelect;
        _names = names;
        _glyphToCid = glyphToCid;
        Encoding = encoding;
        _nameToGlyph = nameToGlyph;
        if (glyphToCid is not null)
        {
            var maxCid = 0;
            foreach (var cid in glyphToCid) maxCid = Math.Max(maxCid, cid);
            maxCid = Math.Min(maxCid, 65535);
            if (maxCid < 4 * glyphToCid.Length + 256)
            {
                _cidToGlyph = new int[maxCid + 1];
                Array.Fill(_cidToGlyph, -1);
                for (var i = glyphToCid.Length - 1; i >= 0; i--)
                    if (glyphToCid[i] is >= 0 and <= 65535) _cidToGlyph[glyphToCid[i]] = i;   // for duplicate CIDs the first glyph wins
            }
            else
            {
                var pairs = new List<(int Cid, int Glyph)>(glyphToCid.Length);
                for (var i = 0; i < glyphToCid.Length; i++)
                    if (glyphToCid[i] is >= 0 and <= 65535) pairs.Add((glyphToCid[i], i));
                pairs.Sort();   // by CID; for duplicate CIDs the lower (first) glyph comes first
                var cids = new List<int>(pairs.Count);
                var glyphs = new List<int>(pairs.Count);
                foreach (var (cid, glyph) in pairs)
                    if (cids.Count == 0 || cids[^1] != cid) { cids.Add(cid); glyphs.Add(glyph); }
                _sparseCids = [.. cids];
                _sparseGlyphs = [.. glyphs];
            }
        }
    }

    /// <summary>Font name from the Name INDEX; empty if the INDEX is empty.</summary>
    public string Name { get; }

    /// <summary>Maps font units to text space (1 = one em); usually a scale of 0.001.</summary>
    /// <remarks>For CID-keyed fonts this is the matrix of the first Font DICT if it has one; glyphs of other Font DICTs are converted to it.</remarks>
    public Matrix FontMatrix { get; }

    /// <summary>Whether the font is CID-keyed (the Top DICT contains ROS).</summary>
    public bool IsCidKeyed => _glyphToCid is not null;

    /// <summary>
    /// Whether the data are CFF2 (OpenType 8): no names, charset, encoding or widths of their own — an OpenType font finds its glyphs through
    /// cmap and post and takes the advances from hmtx; the glyphs are drawn in the default instance.
    /// </summary>
    internal bool IsCff2 => _cff2;

    /// <summary>Number of glyphs (entries in the CharStrings INDEX).</summary>
    public int GlyphCount => _charStrings.Count;

    /// <summary>Built-in encoding: code 0–255 → glyph index (0 = none).</summary>
    /// <remarks>All zero for CID-keyed fonts.</remarks>
    public int[] Encoding { get; }

    /// <summary>Finds a glyph by its name.</summary>
    /// <param name="name">Glyph name, e.g. "A" or ".notdef".</param>
    /// <returns>The glyph index, or -1 if the font has no such name (always -1 for CID-keyed fonts).</returns>
    public int GlyphByName(string name) => _nameToGlyph is not null && _nameToGlyph.TryGetValue(name, out var g) ? g : -1;

    /// <summary>Returns the name of a glyph.</summary>
    /// <param name="glyph">Glyph index.</param>
    /// <returns>The glyph name, or null for CID-keyed fonts and indices out of range.</returns>
    public string? GlyphName(int glyph) => _names is not null && glyph >= 0 && glyph < _names.Length ? _names[glyph] : null;

    /// <summary>Maps a CID to a glyph index; without a CID mapping the CID is the glyph index.</summary>
    /// <param name="cid">Character identifier.</param>
    /// <returns>The glyph index, or -1 if the CID has no glyph.</returns>
    public int GlyphByCid(int cid)
    {
        if (_cidToGlyph is not null) return (uint)cid < (uint)_cidToGlyph.Length ? _cidToGlyph[cid] : -1;
        if (_sparseCids is not null)
        {
            var i = Array.BinarySearch(_sparseCids, cid);
            return i >= 0 ? _sparseGlyphs![i] : -1;
        }
        return cid >= 0 && cid < GlyphCount ? cid : -1;
    }

    /// <summary>The CID of a glyph in a CID-keyed font (0 for indices out of range); for other fonts the glyph index.</summary>
    internal int CidOf(int glyph) => _glyphToCid is { } cids ? (uint)glyph < (uint)cids.Length ? cids[glyph] : 0 : glyph;

    /// <summary>Returns the outline of a glyph in font units.</summary>
    /// <param name="glyph">Glyph index.</param>
    /// <returns>The outline; <see cref="GlyphOutline.Empty"/> for indices out of range and for charstrings that cannot be executed.</returns>
    public GlyphOutline Outline(int glyph) => Glyph(glyph).Outline;

    /// <summary>Returns the advance width of a glyph in font units.</summary>
    /// <param name="glyph">Glyph index.</param>
    /// <returns>The advance width; 0 for indices out of range, the default width of the Private DICT for charstrings that cannot be executed.</returns>
    public double AdvanceWidth(int glyph) => Glyph(glyph).Width;

    private (GlyphOutline Outline, double Width) Glyph(int glyph)
    {
        if (glyph < 0 || glyph >= GlyphCount) return (GlyphOutline.Empty, 0);
        // a component of an accented character (seac) not through the cache: one cut at the seac limit was kept there as the glyph itself,
        // and a glyph naming itself came out empty (Adobe TN 5177, Appendix C; No. 1148)
        if (CharStringInterpreter.InSeac) return Build(glyph);
        if (_cache.TryGetValue(glyph, out var cached)) return cached;
        return _cache.GetOrAdd(glyph, Build);

        (GlyphOutline Outline, double Width) Build(int g)
        {
            try
            {
                var (outline, width) = new CharStringInterpreter(this, PrivateFor(g)).Run(g);
                // Glyph of a Font DICT with its own FontMatrix: convert it to the scale of the font
                if (_fdRelative is { } relative && _fdSelect is not null && g < _fdSelect.Length && _fdSelect[g] < relative.Length
                    && relative[_fdSelect[g]] is { } m)
                {
                    var scaled = new GlyphOutline();
                    scaled.Append(outline, m);
                    return (scaled.Finish(), width * m.A);
                }
                return (outline, width);
            }
            catch (Exception e) when (e is FontFormatException or IndexOutOfRangeException or InvalidOperationException)
            {
                return (GlyphOutline.Empty, PrivateFor(g).DefaultWidth);
            }
        }
    }

    private PrivateDict PrivateFor(int glyph) =>
        _fdSelect is not null && glyph < _fdSelect.Length && _fdSelect[glyph] < _privates.Length ? _privates[_fdSelect[glyph]] : _privates[0];

    // ------------------------------------------------------------ Parsing

    /// <summary>Parses a CFF font.</summary>
    /// <param name="data">The CFF data; the font keeps a reference to it and reads charstrings from it on demand.</param>
    /// <returns>The parsed font.</returns>
    /// <exception cref="FontFormatException">The data has no Top DICT or no CharStrings, or an INDEX runs past the end of the data.</exception>
    public static CffFont Parse(ArraySegment<byte> data)
    {
        var r = new FontReader(data);
        // a major version other than 1 is a format this reader does not know (TN 5176, 6; CFF2 data in a "CFF " table) — before read as 1
        // (No. 1148); the minor version may be higher
        if (r.U8() != 1) throw new FontFormatException("CFF: unbekannte Hauptversion");
        r.U8();                             // minor
        var headerSize = r.U8();
        r.Position = headerSize;
        var names = CffIndex.Read(data, ref r);
        var topDicts = CffIndex.Read(data, ref r);
        var strings = CffIndex.Read(data, ref r);
        var globalSubrs = CffIndex.Read(data, ref r);
        if (topDicts.Count == 0) throw new FontFormatException("CFF ohne Top DICT");

        var name = names.Count > 0 ? System.Text.Encoding.ASCII.GetString(names.Item(data, 0)) : "";
        var top = Dict.Parse(topDicts.Item(data, 0));
        string Sid(int sid) => sid < FontTables.CffStandardStrings.Length ? FontTables.CffStandardStrings[sid]
            : sid - FontTables.CffStandardStrings.Length < strings.Count ? System.Text.Encoding.Latin1.GetString(strings.Item(data, sid - FontTables.CffStandardStrings.Length)) : "";

        var topMatrix = top.Matrix(1207);
        var fontMatrix = topMatrix ?? new Matrix(0.001, 0, 0, 0.001, 0, 0);
        if (top.Int(17) is not { } charStringsOffset) throw new FontFormatException("CFF ohne CharStrings");
        r.Position = charStringsOffset;
        var charStrings = CffIndex.Read(data, ref r);
        var glyphCount = charStrings.Count;

        var isCid = top.Has(1230);
        PrivateDict[] privates;
        ushort[]? fdSelect = null;
        Matrix?[]? fdRelative = null;
        if (isCid && top.Int(1236) is { } fdArrayOffset)
        {
            r.Position = fdArrayOffset;
            var fdArray = CffIndex.Read(data, ref r);
            // only the Font DICTs FDSelect can name (a Card8: 256), each Private DICT read once — 65 535 Font DICTs naming the same Private
            // DICT with 65 535 subroutines held 256 KB each, gigabytes from a font under a megabyte (docs/FEHLERGEDAECHTNIS.md, No. 1136)
            var fdCount = Math.Min(fdArray.Count, MaxFontDicts);
            privates = new PrivateDict[Math.Max(1, fdCount)];
            var read = new Dictionary<(double, double), PrivateDict>();
            // FontMatrix per Font DICT (e.g. the test file text_clip_cff_cid): combined with the Top DICT matrix if the
            // Top DICT has one (as common font engines do); using only the Top DICT matrix draws such glyphs at the wrong size
            var fdMatrices = new Matrix?[privates.Length];
            for (var i = 0; i < fdCount; i++)
            {
                var fd = Dict.Parse(fdArray.Item(data, i));
                var location = fd.Get(18) is { Length: 2 } at ? (at[0], at[1]) : (-1d, -1d);
                if (!read.TryGetValue(location, out var priv)) read[location] = priv = PrivateDict.Read(data, fd);
                privates[i] = priv;
                if (fd.Matrix(1207) is { } sub) fdMatrices[i] = topMatrix is { } t ? sub * t : sub;
            }
            if (fdArray.Count == 0) privates[0] = PrivateDict.Empty;
            if (top.Int(1237) is { } fdSelectOffset) fdSelect = Array.ConvertAll(ReadFdSelect(data, fdSelectOffset, glyphCount), b => (ushort)b);
            // The first Font DICT determines the FontMatrix of the font; the others are converted relative to it per glyph
            if (fdMatrices[0] is { } first) fontMatrix = first;
            var inverse = fontMatrix.Invert();
            for (var i = 0; i < fdMatrices.Length; i++)
            {
                var relative = (fdMatrices[i] ?? topMatrix ?? new Matrix(0.001, 0, 0, 0.001, 0, 0)) * inverse;
                if (Math.Abs(relative.A - 1) + Math.Abs(relative.B) + Math.Abs(relative.C) + Math.Abs(relative.D - 1) + Math.Abs(relative.E) + Math.Abs(relative.F) > 1e-9)
                    (fdRelative ??= new Matrix?[fdMatrices.Length])[i] = relative;
            }
        }
        else
        {
            privates = [PrivateDict.Read(data, top)];
        }

        // Charset: glyph → SID (or CID)
        var sids = ReadCharset(data, top.Int(15) ?? 0, glyphCount, isCid);
        string[]? glyphNames = null;
        Dictionary<string, int>? nameToGlyph = null;
        int[]? glyphToCid = null;
        if (isCid)
        {
            glyphToCid = sids;
        }
        else
        {
            glyphNames = new string[glyphCount];
            for (var i = 0; i < glyphCount; i++) glyphNames[i] = i == 0 ? ".notdef" : Sid(sids[i]);
            nameToGlyph = new Dictionary<string, int>(glyphCount, StringComparer.Ordinal);
            for (var i = 0; i < glyphNames.Length; i++) nameToGlyph.TryAdd(glyphNames[i], i);
        }

        var encoding = isCid ? new int[256] : ReadEncoding(data, top.Int(16) ?? 0, nameToGlyph!, Sid);
        return new CffFont(data, name, fontMatrix, charStrings, globalSubrs, privates, fdSelect, glyphNames, nameToGlyph, glyphToCid, encoding)
        {
            _fdRelative = fdRelative,
        };
    }

    /// <summary>The glyphs a CFF2 table may hold: numGlyphs of maxp is a uint16 and must equal the count of the CharStringINDEX (OpenType 8.10.2).</summary>
    private const int MaxCff2Glyphs = 65535;

    /// <summary>
    /// Parses the "CFF2" table of an OpenType font (OpenType 8): header, Top DICT, GlobalSubrINDEX, CharStringINDEX, FontDICTINDEX with the
    /// Private DICTs and their local subroutines, FontDICTSelect (formats 0, 3, 4) and of the VariationStore the number of regions of each
    /// ItemVariationData (k, for blend). The glyphs are drawn in the default instance: blend leaves the default values and drops the deltas.
    /// </summary>
    /// <remarks>
    /// Tolerant where a lenient reader draws: without FontDICTINDEX, or with an empty one, the glyphs draw without local subroutines; a
    /// FontDICTSelect of an unknown format leaves every glyph with the first Font DICT. Sizes from the data are bounded as for CFF (at most 256
    /// Font DICTs, each Private DICT read once — docs/FEHLERGEDAECHTNIS.md, No. 1136; an INDEX is checked against the data before its offsets
    /// are read; per glyph the work, depth and stack limits of the charstring interpreter; No. 1158).
    /// </remarks>
    /// <param name="data">The CFF2 table; the font keeps a reference to it and reads charstrings from it on demand.</param>
    /// <param name="unitsPerEm">unitsPerEm of the head table: without a FontMatrix in the Top DICT the glyphs are scaled by its inverse, as common
    /// font engines do (the FontMatrix may only be left out at 1000 units per em, OpenType 8.7.2).</param>
    /// <returns>The parsed font (<see cref="IsCff2"/>).</returns>
    /// <exception cref="FontFormatException">The major version is not 2, the Top DICT lies outside the table, there is no CharStringINDEX, or an
    /// INDEX runs past the end of the data or counts more than 65 535 glyphs.</exception>
    internal static CffFont ParseCff2(ArraySegment<byte> data, int unitsPerEm)
    {
        var r = new FontReader(data);
        if (r.U8() != 2) throw new FontFormatException("CFF2: unbekannte Hauptversion");
        r.U8();                             // minor
        var headerSize = r.U8();
        int topSize = r.U16();
        // the Top DICT starts at headerSize (a later version may put data before it; OpenType 8.6)
        if (headerSize < 5 || headerSize > data.Count - topSize) throw new FontFormatException("CFF2: TopDICT außerhalb der Tabelle");
        var top = Dict.ParseCff2(data.AsSpan(headerSize, topSize), null);
        r.Position = headerSize + topSize;
        var globalSubrs = CffIndex.Read2(data, ref r);
        if (top.Int(17) is not { } charStringsOffset) throw new FontFormatException("CFF2 ohne CharStrings");
        r.Position = charStringsOffset;
        var charStrings = CffIndex.Read2(data, ref r);
        if (charStrings.Count > MaxCff2Glyphs) throw new FontFormatException("CFF2: zu viele Glyphen");
        var regionCounts = top.Int(24) is { } storeOffset ? ReadRegionCounts(data, storeOffset) : null;

        var privates = new List<PrivateDict>();
        if (top.Int(1236) is { } fontDictsOffset)
        {
            try
            {
                r.Position = fontDictsOffset;
                var fontDicts = CffIndex.Read2(data, ref r);
                var read = new Dictionary<(double, double), PrivateDict>();
                for (var i = 0; i < Math.Min(fontDicts.Count, MaxFontDicts); i++)
                {
                    var fd = Dict.ParseCff2(fontDicts.Item(data, i), null);
                    var location = fd.Get(18) is { Length: 2 } at ? (at[0], at[1]) : (-1d, -1d);
                    if (!read.TryGetValue(location, out var priv)) read[location] = priv = PrivateDict.ReadCff2(data, fd, regionCounts);
                    privates.Add(priv);
                }
            }
            catch (FontFormatException)
            {
                // a FontDICTINDEX beyond the table: the glyphs draw without local subroutines
            }
        }
        if (privates.Count == 0) privates.Add(PrivateDict.Empty);
        var fdSelect = top.Int(1237) is { } fdSelectOffset ? ReadFdSelectCff2(data, fdSelectOffset, charStrings.Count) : null;

        var topMatrix = top.Matrix(1207);
        var fontMatrix = topMatrix ?? Matrix.Scale(1.0 / unitsPerEm, 1.0 / unitsPerEm);
        return new CffFont(data, "", fontMatrix, charStrings, globalSubrs, [.. privates], fdSelect, null, null, null, new int[256])
        {
            _cff2 = true,
            _regionCounts = regionCounts,
            _topMatrix = topMatrix is null ? null : top.Get(1207),
        };
    }

    /// <summary>
    /// The number of regions of each ItemVariationData of a CFF2 VariationStore (OpenType 8.11.2: a uint16 length, then an ItemVariationStore
    /// whose offsets count from its own start); k of blend. An entry that cannot be read is -1; null if the store itself cannot be read.
    /// </summary>
    private static int[]? ReadRegionCounts(ArraySegment<byte> data, int offset)
    {
        try
        {
            var r = new FontReader(data, offset);
            r.U16();                        // length of the ItemVariationStore
            var start = r.Position;
            if (r.U16() != 1) return null;  // format 1 only
            r.U32();                        // VariationRegionList: the regions do not matter in the default instance
            int count = r.U16();
            var counts = new int[count];
            for (var i = 0; i < count; i++)
            {
                var at = (long)start + r.U32();
                var d = new FontReader(data, at is >= 0 and <= int.MaxValue ? (int)at : -1);
                if (!d.CanRead(6))
                {
                    counts[i] = -1;
                    continue;
                }
                d.Skip(4);                  // itemCount and wordDeltaCount: CFF2 keeps the deltas in its charstrings (8.11.2)
                counts[i] = d.U16();        // regionIndexCount
            }
            return counts;
        }
        catch (FontFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// FontDICTSelect of CFF2 (OpenType 8.8.3): format 0 (a byte per glyph), 3 (ranges of uint16 glyphs with a uint8 index) and 4 (ranges of
    /// uint32 glyphs with a uint16 index). Each glyph takes the first range that covers it: ranges running backwards cover nothing again, so
    /// the work stays one step per glyph and range however many ranges a damaged table counts. Unknown formats and missing data: index 0.
    /// </summary>
    private static ushort[] ReadFdSelectCff2(ArraySegment<byte> data, int offset, int glyphCount)
    {
        var fd = new ushort[glyphCount];
        try
        {
            var r = new FontReader(data, offset);
            var format = r.U8();
            if (format == 0)
            {
                for (var g = 0; g < glyphCount; g++) fd[g] = r.U8();
            }
            else if (format is 3 or 4)
            {
                long ranges = format == 3 ? r.U16() : r.U32();
                long first = format == 3 ? r.U16() : r.U32();
                long covered = 0;
                for (long i = 0; i < ranges && covered < glyphCount; i++)
                {
                    var index = format == 3 ? r.U8() : r.U16();
                    long next = format == 3 ? r.U16() : r.U32();
                    for (var g = Math.Max(first, covered); g < next && g < glyphCount; g++) fd[g] = index;
                    covered = Math.Max(covered, Math.Min(next, glyphCount));
                    first = next;
                }
            }
        }
        catch (FontFormatException)
        {
        }
        return fd;
    }

    internal static int[] ReadCharset(ArraySegment<byte> data, int offset, int glyphCount, bool isCid)
    {
        var sids = new int[glyphCount];
        if (offset is 0 or 1 or 2 && !isCid)
        {
            // Predefined: ISOAdobe (SID = glyph), Expert, ExpertSubset — the latter two stored as names
            string[]? predefined = offset == 1 ? FontTables.ExpertCharset : offset == 2 ? FontTables.ExpertSubsetCharset : null;
            for (var i = 0; i < glyphCount; i++)
                sids[i] = predefined is null ? i : i < predefined.Length ? Array.IndexOf(FontTables.CffStandardStrings, predefined[i]) is var s and >= 0 ? s : 0 : 0;
            return sids;
        }
        if (offset is 0 && isCid)
        {
            for (var i = 0; i < glyphCount; i++) sids[i] = i;
            return sids;
        }
        try
        {
            var r = new FontReader(data, offset);
            var format = r.U8();
            var g = 1;
            switch (format)
            {
                case 0:
                    while (g < glyphCount) sids[g++] = r.U16();
                    break;
                case 1:
                case 2:
                    while (g < glyphCount)
                    {
                        int first = r.U16();
                        int left = format == 1 ? r.U8() : r.U16();
                        for (var k = 0; k <= left && g < glyphCount; k++) sids[g++] = first + k;
                    }
                    break;
            }
        }
        catch (FontFormatException)
        {
            // the remaining glyphs stay .notdef
        }
        return sids;
    }

    private static int[] ReadEncoding(ArraySegment<byte> data, int offset, Dictionary<string, int> nameToGlyph, Func<int, string> sid)
    {
        var codes = new int[256];

        if (offset is 0 or 1)
        {
            var table = offset == 0 ? FontTables.StandardEncoding : FontTables.ExpertEncoding;
            for (var c = 0; c < 256; c++)
                if (table[c].Length > 0 && nameToGlyph.TryGetValue(table[c], out var g)) codes[c] = g;
            return codes;
        }
        try
        {
            var r = new FontReader(data, offset);
            var format = r.U8();
            switch (format & 0x7F)
            {
                case 0:
                    int n = r.U8();
                    for (var i = 1; i <= n; i++) codes[r.U8()] = i;
                    break;
                case 1:
                    int ranges = r.U8();
                    var glyph = 1;
                    for (var i = 0; i < ranges; i++)
                    {
                        int first = r.U8();
                        int left = r.U8();
                        for (var k = 0; k <= left && first + k < 256; k++) codes[first + k] = glyph++;
                    }
                    break;
            }
            if ((format & 0x80) != 0)
            {
                int supplements = r.U8();
                for (var i = 0; i < supplements; i++)
                {
                    int code = r.U8();
                    var name = sid(r.U16());
                    if (nameToGlyph.TryGetValue(name, out var g)) codes[code] = g;
                }
            }
        }
        catch (FontFormatException)
        {
        }
        return codes;
    }

    internal static byte[] ReadFdSelect(ArraySegment<byte> data, int offset, int glyphCount)
    {
        var fd = new byte[glyphCount];
        try
        {
            var r = new FontReader(data, offset);
            var format = r.U8();
            if (format == 0)
            {
                for (var g = 0; g < glyphCount; g++) fd[g] = r.U8();
            }
            else if (format == 3)
            {
                int ranges = r.U16();
                int first = r.U16();
                // ranges that run up and down (a crafted font) would fill the glyphs again and again, up to some 2 000 million steps: a budget
                // of four fillings of every glyph — a valid font fills each glyph once — ends the reading, the rest stays Font DICT 0
                // (docs/FEHLERGEDAECHTNIS.md, No. 1170)
                var budget = 4L * Math.Max(glyphCount, 1);
                for (var i = 0; i < ranges && budget > 0; i++)
                {
                    var index = r.U8();
                    int next = r.U16();
                    var end = Math.Min(next, glyphCount);
                    if (end > first)
                    {
                        if (end - first > budget) end = first + (int)budget;
                        fd.AsSpan(first, end - first).Fill(index);
                        budget -= end - first;
                    }
                    first = next;
                }
            }
        }
        catch (FontFormatException)
        {
        }
        return fd;
    }

    // ------------------------------------------------------------ Building blocks

    /// <summary>INDEX: count, offsets, data (Adobe TN 5176, section 5).</summary>
    internal readonly struct CffIndex(int count, int[] offsets, int dataStart)
    {
        public int Count { get; } = count;

        public static CffIndex Read(ArraySegment<byte> data, ref FontReader r)
        {
            int count = r.U16();
            if (count == 0) return new CffIndex(0, [1], r.Position);
            int offSize = r.U8();
            var offsets = new int[count + 1];
            for (var i = 0; i <= count; i++) offsets[i] = r.Offset(offSize);
            var dataStart = r.Position - 1;
            r.Position = dataStart + offsets[count];
            if (r.Position > data.Count || r.Position < 0) throw new FontFormatException("CFF-INDEX zu lang");
            return new CffIndex(count, offsets, dataStart);
        }

        /// <summary>
        /// An INDEX of CFF2 (OpenType 8.5.2): the count is a uint32 (an empty INDEX is the count alone). The offsets must fit into the data
        /// before they are read — a count of four billion in a short table must not allocate their array (No. 1158).
        /// </summary>
        public static CffIndex Read2(ArraySegment<byte> data, ref FontReader r)
        {
            var count = r.U32();
            if (count == 0) return new CffIndex(0, [1], r.Position);
            int offSize = r.U8();
            if (offSize is < 1 or > 4 || (count + 1L) * offSize > data.Count - r.Position) throw new FontFormatException("CFF2-INDEX zu lang");
            var offsets = new int[count + 1];
            for (var i = 0; i <= count; i++) offsets[i] = r.Offset(offSize);
            var dataStart = r.Position - 1;
            r.Position = dataStart + offsets[count];
            if (r.Position > data.Count || r.Position < 0) throw new FontFormatException("CFF2-INDEX zu lang");
            return new CffIndex((int)count, offsets, dataStart);
        }

        public ReadOnlySpan<byte> Item(ArraySegment<byte> data, int i)
        {
            if (i < 0 || i >= Count) return [];
            var start = dataStart + offsets[i];
            var end = dataStart + offsets[i + 1];
            if (start < 0 || end > data.Count || end < start) return [];
            return data.AsSpan(start, end - start);
        }
    }

    /// <summary>DICT: operator → operands (Adobe TN 5176, section 4).</summary>
    internal sealed class Dict
    {
        private readonly Dictionary<int, double[]> _entries = [];

        public static Dict Parse(ReadOnlySpan<byte> bytes)
        {
            var dict = new Dict();
            var operands = new List<double>();
            var i = 0;
            while (i < bytes.Length)
            {
                int b0 = bytes[i++];
                if (b0 <= 21)
                {
                    var op = b0 == 12 && i < bytes.Length ? 1200 + bytes[i++] : b0;
                    dict._entries[op] = [.. operands];
                    operands.Clear();
                }
                else if (b0 == 28 && i + 1 < bytes.Length)
                {
                    operands.Add((short)(bytes[i] << 8 | bytes[i + 1]));
                    i += 2;
                }
                else if (b0 == 29 && i + 3 < bytes.Length)
                {
                    operands.Add(bytes[i] << 24 | bytes[i + 1] << 16 | bytes[i + 2] << 8 | bytes[i + 3]);
                    i += 4;
                }
                else if (b0 == 30)
                {
                    operands.Add(Real(bytes, ref i));
                }
                else if (b0 is >= 32 and <= 246)
                {
                    operands.Add(b0 - 139);
                }
                else if (b0 is >= 247 and <= 250 && i < bytes.Length)
                {
                    operands.Add((b0 - 247) * 256 + bytes[i++] + 108);
                }
                else if (b0 is >= 251 and <= 254 && i < bytes.Length)
                {
                    operands.Add(-(b0 - 251) * 256 - bytes[i++] - 108);
                }
            }
            return dict;
        }

        /// <summary>
        /// A DICT of CFF2 (OpenType 8.5.1): operators 0–24 and 12 x, the reserved bytes 25–27, 31 and 255 as operators that only clear the stack
        /// (8.4.1), at most 513 operands (further numbers are dropped). vsindex (22) is kept as an entry and selects the regions for the blends
        /// after it; blend (23) works in the default instance: of n default values, n·k deltas and n it leaves the n default values for the next
        /// operator (8.9.3). A blend whose regions are unknown (no VariationStore, vsindex out of range) or that lacks operands clears the stack.
        /// </summary>
        /// <param name="bytes">The DICT data.</param>
        /// <param name="regionCounts">The regions of each ItemVariationData (k); null without VariationStore.</param>
        public static Dict ParseCff2(ReadOnlySpan<byte> bytes, int[]? regionCounts)
        {
            var dict = new Dict();
            var operands = new List<double>();
            var vsindex = 0;
            var i = 0;
            void Add(double v)
            {
                if (operands.Count < MaxStackCff2) operands.Add(v);
            }
            while (i < bytes.Length)
            {
                int b0 = bytes[i++];
                if (b0 <= 27 || b0 is 31 or 255)
                {
                    var op = b0 == 12 && i < bytes.Length ? 1200 + bytes[i++] : b0;
                    if (op == 23)
                    {
                        var n = operands.Count > 0 ? operands[^1] : -1;
                        var k = regionCounts is { } counts && vsindex >= 0 && vsindex < counts.Length ? counts[vsindex] : -1;
                        if (k >= 0 && n >= 0 && n == Math.Floor(n) && n * (k + 1) + 1 <= operands.Count)
                            operands.RemoveRange(operands.Count - (int)(n * k + 1), (int)(n * k + 1));
                        else operands.Clear();
                        continue;
                    }
                    if (op == 22 && operands.Count > 0) vsindex = (int)Math.Clamp(operands[^1], -1, int.MaxValue);
                    if (op is not (25 or 26 or 27 or 31 or 255)) dict._entries[op] = [.. operands];
                    operands.Clear();
                }
                else if (b0 == 28 && i + 1 < bytes.Length)
                {
                    Add((short)(bytes[i] << 8 | bytes[i + 1]));
                    i += 2;
                }
                else if (b0 == 29 && i + 3 < bytes.Length)
                {
                    Add(bytes[i] << 24 | bytes[i + 1] << 16 | bytes[i + 2] << 8 | bytes[i + 3]);
                    i += 4;
                }
                else if (b0 == 30)
                {
                    Add(Real(bytes, ref i));
                }
                else if (b0 is >= 32 and <= 246)
                {
                    Add(b0 - 139);
                }
                else if (b0 is >= 247 and <= 250 && i < bytes.Length)
                {
                    Add((b0 - 247) * 256 + bytes[i++] + 108);
                }
                else if (b0 is >= 251 and <= 254 && i < bytes.Length)
                {
                    Add(-(b0 - 251) * 256 - bytes[i++] - 108);
                }
            }
            return dict;
        }

        private static double Real(ReadOnlySpan<byte> bytes, ref int i)
        {
            var sb = new System.Text.StringBuilder();
            while (i < bytes.Length)
            {
                var b = bytes[i++];
                for (var shift = 4; shift >= 0; shift -= 4)
                {
                    var nibble = (b >> shift) & 0xF;
                    switch (nibble)
                    {
                        case <= 9: sb.Append((char)('0' + nibble)); break;
                        case 0xA: sb.Append('.'); break;
                        case 0xB: sb.Append('E'); break;
                        case 0xC: sb.Append("E-"); break;
                        case 0xE: sb.Append('-'); break;
                        case 0xF:
                            // beyond double ("1E400" reads as infinity): as unreadable, 0 (docs/FEHLERGEDAECHTNIS.md, No. 933)
                            return double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) &&
                                   double.IsFinite(v) ? v : 0;
                    }
                }
            }
            return 0;
        }

        /// <summary>All entries (operator → operands), for rewriting the DICT (<see cref="CffSubsetter"/>).</summary>
        public IEnumerable<KeyValuePair<int, double[]>> Entries => _entries;

        public bool Has(int op) => _entries.ContainsKey(op);

        public double[]? Get(int op) => _entries.GetValueOrDefault(op);

        public int? Int(int op) => _entries.TryGetValue(op, out var v) && v.Length > 0 ? (int)v[^1] : null;

        public double? Number(int op) => _entries.TryGetValue(op, out var v) && v.Length > 0 ? v[0] : null;

        public Matrix? Matrix(int op) =>
            _entries.TryGetValue(op, out var v) && v.Length == 6 && v.All(double.IsFinite) && Math.Abs(v[0] * v[3] - v[1] * v[2]) > 1e-12
                ? new Matrix(v[0], v[1], v[2], v[3], v[4], v[5]) : null;
    }

    internal sealed class PrivateDict
    {
        public static readonly PrivateDict Empty = new();

        public CffIndex Subrs;
        public bool HasSubrs;
        public double DefaultWidth;
        public double NominalWidth;

        /// <summary>CFF2: the ItemVariationData the charstrings of this Private DICT blend with unless they choose one (vsindex, OpenType 8.9.3).</summary>
        public int VsIndex;

        /// <summary>CFF2: the Private DICT as read in the default instance (for rewriting it, <see cref="Cff2Scrubber"/>); null for CFF.</summary>
        public Dict? Source;

        /// <summary>
        /// Reads the Private DICT of a CFF2 Font DICT (operator 18: size, offset from the start of the table; 0 0 for an empty one, OpenType 8.8.4)
        /// with its LocalSubrINDEX (operator 19, offset from the start of the Private DICT) and vsindex. A Private DICT or subroutine INDEX
        /// beyond the table is left out.
        /// </summary>
        public static PrivateDict ReadCff2(ArraySegment<byte> data, Dict owner, int[]? regionCounts)
        {
            if (owner.Get(18) is not { Length: 2 } location) return Empty;
            var size = location[0];
            var offset = location[1];
            if (offset < 0 || size <= 0 || offset > data.Count - size) return Empty;
            var dict = Dict.ParseCff2(data.AsSpan((int)offset, (int)size), regionCounts);
            var result = new PrivateDict { VsIndex = dict.Int(22) ?? 0, Source = dict };
            if (dict.Int(19) is { } subrs && offset + subrs is >= 0 and <= int.MaxValue)
            {
                try
                {
                    var r = new FontReader(data, (int)(offset + subrs));
                    result.Subrs = CffIndex.Read2(data, ref r);
                    result.HasSubrs = true;
                }
                catch (FontFormatException)
                {
                }
            }
            return result;
        }

        /// <summary>Reads the Private DICT of a Top or Font DICT (operator 18: size, offset).</summary>
        public static PrivateDict Read(ArraySegment<byte> data, Dict owner)
        {
            if (owner.Get(18) is not { Length: 2 } location) return Empty;
            var size = (int)location[0];
            var offset = (int)location[1];
            if (offset < 0 || size < 0 || offset > data.Count - size) return Empty;
            var dict = Dict.Parse(data.AsSpan(offset, size));
            var result = new PrivateDict
            {
                DefaultWidth = dict.Number(20) ?? 0,
                NominalWidth = dict.Number(21) ?? 0,
            };
            if (dict.Int(19) is { } subrs)
            {
                try
                {
                    var r = new FontReader(data, offset + subrs);
                    result.Subrs = CffIndex.Read(data, ref r);
                    result.HasSubrs = true;
                }
                catch (FontFormatException)
                {
                }
            }
            return result;
        }
    }

    // ------------------------------------------------------------ Type 2 charstrings

    internal static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    // ------------------------------------------------------------ CFF2: rewriting (Cff2Scrubber)

    /// <summary>The CFF2 Font DICT of a glyph (an index into the Font DICTs read; 0 where FontDICTSelect names none of them).</summary>
    internal int FontDictOf(int glyph) => _fdSelect is not null && (uint)glyph < (uint)_fdSelect.Length && _fdSelect[glyph] < _privates.Length ? _fdSelect[glyph] : 0;

    /// <summary>The number of Font DICTs read (at least one).</summary>
    internal int FontDictCount => _privates.Length;

    /// <summary>CFF2: the Private DICT of a Font DICT in the default instance; null for an empty one.</summary>
    internal Dict? PrivateSource(int fontDict) => _privates[fontDict].Source;

    /// <summary>CFF2: the operands of the Top DICT's FontMatrix as written; null if it has none.</summary>
    internal double[]? TopMatrixOperands => _topMatrix;

    /// <summary>
    /// CFF2: the charstring of a glyph in the default instance without subroutines — what the interpreter executes, written out: every operator
    /// that draws or hints with the numbers it finds on the stack (and the mask bytes of hintmask and cntrmask), the subroutines in place of
    /// callsubr and callgsubr, without blend (the default values stay, the deltas go), vsindex, return and endchar. Read again it gives the same
    /// outline and keeps the hints; it carries no variation data and no part of another glyph (OpenType 8.10, 8.11.3). A glyph whose charstring
    /// cannot be executed is empty (as drawn); one that would grow beyond 65 535 bytes is written from its outline, without hints.
    /// </summary>
    /// <param name="glyph">The glyph index.</param>
    /// <returns>The charstring; empty for a glyph without outline.</returns>
    internal byte[] StaticCharString(int glyph)
    {
        if (!_cff2 || glyph < 0 || glyph >= GlyphCount) return [];
        try
        {
            var output = new List<byte>();
            new CharStringInterpreter(this, PrivateFor(glyph), output).Run(glyph);
            if (output.Count <= MaxCharStringBytes) return [.. output];
        }
        catch (Exception e) when (e is FontFormatException or IndexOutOfRangeException or InvalidOperationException)
        {
            return [];
        }
        return CffBuilder.Path(Outline(glyph));
    }

    /// <summary>A charstring holds at most 65 535 bytes (Adobe TN 5177, Appendix B; OpenType 8.10.1).</summary>
    internal const int MaxCharStringBytes = 65535;

    /// <summary>The number of regions (k) of an ItemVariationData of a CFF2 VariationStore; -1 without such an ItemVariationData.</summary>
    private int RegionCount(int vsindex) =>
        _regionCounts is { } counts && vsindex >= 0 && vsindex < counts.Length ? counts[vsindex] : -1;

    /// <summary>
    /// Executes a Type 2 charstring (Adobe TN 5177) and records the outline. For CFF2 (OpenType 8.10) with a stack of 513 numbers, without
    /// widths, with blend and vsindex in the default instance; the arithmetic operators of Type 2 are reserved there. With
    /// <paramref name="emit"/> it also writes the static charstring (<see cref="StaticCharString"/>).
    /// </summary>
    private sealed class CharStringInterpreter(CffFont font, PrivateDict priv, List<byte>? emit = null)
    {
        private readonly GlyphOutline _outline = new();
        // Work limit per glyph: subroutines that call each other many times grow exponentially despite the depth limit
        private const int MaxSteps = 200_000;
        private int _steps;
        private readonly double[] _stack = new double[font._cff2 ? MaxStackCff2 : MaxStack];
        private readonly double[] _transient = new double[32];
        private int _sp;
        private double _x, _y;
        private int _stems;
        private bool _widthDone;
        private double _width;
        private bool _ended;
        private int _vsindex = priv.VsIndex;   // CFF2: the ItemVariationData of blend
        private bool _blended;                 // CFF2: a blend was executed (vsindex may only come before, OpenType 8.11.3)

        public (GlyphOutline, double) Run(int glyph)
        {
            _width = priv.DefaultWidth;
            Execute(font._charStrings.Item(font._data, glyph), 0);
            return (_outline.Finish(), _width);
        }

        /// <summary>The width precedes the first stack-clearing operator when that operator has one argument more than it takes; CFF2 has no widths (OpenType 8.10.1).</summary>
        private void Width(bool hasExtra)
        {
            if (_widthDone || font._cff2) return;
            _widthDone = true;
            if (hasExtra && _sp > 0)
            {
                _width = priv.NominalWidth + _stack[0];
                Array.Copy(_stack, 1, _stack, 0, _sp - 1);
                _sp--;
            }
        }

        private void Push(double v)
        {
            if (_sp < _stack.Length) _stack[_sp++] = v;
            // CFF2: more than 513 numbers make the charstring invalid (OpenType 8.4.1), as common font engines treat it: the glyph is empty
            else if (font._cff2) throw new FontFormatException("CFF2: Stapel übergelaufen");
        }

        /// <summary>
        /// CFF2 blend in the default instance (OpenType 8.11.3): n on top, below it n default values and n groups of k deltas; the default values
        /// stay, the deltas and n go — every region scalar is 0 at the default. Without the regions (no VariationStore, vsindex beyond it) or
        /// without the operands the charstring is invalid and the glyph empty, as in common font engines.
        /// </summary>
        private void Blend()
        {
            var k = font.RegionCount(_vsindex);
            if (_sp < 1 || k < 0) throw new FontFormatException("CFF2: blend ohne Regionen");
            var n = _stack[_sp - 1];
            if (n < 0 || n != Math.Floor(n) || n * (k + 1) + 1 > _sp) throw new FontFormatException("CFF2: blend ohne Operanden");
            _sp -= (int)(n * k + 1);
            _blended = true;
        }

        /// <summary>Writes the numbers on the stack and an operator into the static charstring (<see cref="StaticCharString"/>).</summary>
        private void Emit(int op, ReadOnlySpan<byte> mask = default)
        {
            // beyond the limit nothing more is written: the glyph is then written from its outline (many subroutine calls must not make the
            // list grow far beyond it first)
            if (emit is null || emit.Count > MaxCharStringBytes) return;
            for (var k = 0; k < _sp; k++) CffBuilder.Number(emit, _stack[k]);
            if (op >= 1200)
            {
                emit.Add(12);
                emit.Add((byte)(op - 1200));
            }
            else emit.Add((byte)op);
            foreach (var b in mask) emit.Add(b);
        }

        private void Execute(ReadOnlySpan<byte> code, int depth)
        {
            if (depth > MaxSubrDepth) throw new FontFormatException("Subroutinen zu tief");
            var i = 0;
            while (i < code.Length && !_ended)
            {
                int b0 = code[i++];
                if (++_steps > MaxSteps) throw new FontFormatException("Glyphe zu aufwendig");
                if (b0 >= 32)
                {
                    if (b0 <= 246) Push(b0 - 139);
                    else if (b0 <= 250) Push((b0 - 247) * 256 + At(code, i++) + 108);
                    else if (b0 <= 254) Push(-(b0 - 251) * 256 - At(code, i++) - 108);
                    else
                    {
                        Push((At(code, i) << 24 | At(code, i + 1) << 16 | At(code, i + 2) << 8 | At(code, i + 3)) / 65536.0);
                        i += 4;
                    }
                    continue;
                }
                if (b0 == 28)
                {
                    Push((short)(At(code, i) << 8 | At(code, i + 1)));
                    i += 2;
                    continue;
                }

                switch (b0)
                {
                    case 1 or 3 or 18 or 23:    // hstem, vstem, hstemhm, vstemhm
                        Width(_sp % 2 == 1);
                        Emit(b0);
                        _stems += _sp / 2;
                        _sp = 0;
                        break;
                    case 19 or 20:              // hintmask, cntrmask (preceded by an implicit vstem)
                    {
                        Width(_sp % 2 == 1);
                        _stems += _sp / 2;
                        var maskBytes = (_stems + 7) / 8;
                        if (emit is not null) Emit(b0, MaskBytes(code, i, maskBytes));
                        _sp = 0;
                        i += maskBytes;
                        break;
                    }
                    case 21:                    // rmoveto
                        Width(_sp > 2);
                        Emit(b0);
                        MoveBy(Arg(0), Arg(1));
                        _sp = 0;
                        break;
                    case 22:                    // hmoveto
                        Width(_sp > 1);
                        Emit(b0);
                        MoveBy(Arg(0), 0);
                        _sp = 0;
                        break;
                    case 4:                     // vmoveto
                        Width(_sp > 1);
                        Emit(b0);
                        MoveBy(0, Arg(0));
                        _sp = 0;
                        break;
                    case 5 or 6 or 7 or 8 or 24 or 25 or 26 or 27 or 30 or 31:   // the path operators
                        Emit(b0);
                        DrawPath(b0);
                        _sp = 0;
                        break;
                    case 10:                    // callsubr
                    {
                        if (!priv.HasSubrs || _sp == 0) { _sp = 0; break; }
                        var index = (int)_stack[--_sp] + Bias(priv.Subrs.Count);
                        Execute(priv.Subrs.Item(font._data, index), depth + 1);
                        break;
                    }
                    case 29:                    // callgsubr
                    {
                        if (_sp == 0) break;
                        var index = (int)_stack[--_sp] + Bias(font._globalSubrs.Count);
                        Execute(font._globalSubrs.Item(font._data, index), depth + 1);
                        break;
                    }
                    case 11:                    // return
                        return;
                    case 14:                    // endchar (with 4 arguments: seac); CFF2 has neither, and ends the glyph here as common font engines
                        Width(_sp is 1 or 5);
                        if (_sp >= 4 && !font._cff2) Seac(_stack[_sp - 4], _stack[_sp - 3], (int)_stack[_sp - 2], (int)_stack[_sp - 1], depth);
                        _outline.Close();
                        _ended = true;
                        _sp = 0;
                        return;
                    case 15 when font._cff2:    // vsindex: before any blend (OpenType 8.11.3); a later one makes the charstring invalid
                        if (_blended) throw new FontFormatException("CFF2: vsindex nach blend");
                        if (_sp > 0) _vsindex = (int)Math.Clamp(_stack[_sp - 1], -1, int.MaxValue);
                        _sp = 0;
                        break;
                    case 16 when font._cff2:    // blend
                        Blend();
                        break;
                    case 12:
                        Escape(At(code, i++));
                        break;
                    default:
                        _sp = 0;                // reserved: ignore
                        break;
                }
            }
        }

        /// <summary>Draws a path operator (rlineto … hvcurveto) with the numbers on the stack.</summary>
        private void DrawPath(int op)
        {
            switch (op)
            {
                case 5:                     // rlineto
                    for (var k = 0; k + 1 < _sp; k += 2) LineBy(_stack[k], _stack[k + 1]);
                    _sp = 0;
                    break;
                case 6 or 7:                // hlineto, vlineto: alternating
                    var horizontal = op == 6;
                    for (var k = 0; k < _sp; k++)
                    {
                        if (horizontal) LineBy(_stack[k], 0);
                        else LineBy(0, _stack[k]);
                        horizontal = !horizontal;
                    }
                    _sp = 0;
                    break;
                case 8:                     // rrcurveto
                    for (var k = 0; k + 5 < _sp; k += 6)
                        CurveBy(_stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], _stack[k + 4], _stack[k + 5]);
                    _sp = 0;
                    break;
                case 24:                    // rcurveline
                {
                    var k = 0;
                    for (; k + 5 < _sp - 2; k += 6)
                        CurveBy(_stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], _stack[k + 4], _stack[k + 5]);
                    if (k + 1 < _sp) LineBy(_stack[k], _stack[k + 1]);
                    _sp = 0;
                    break;
                }
                case 25:                    // rlinecurve
                {
                    var k = 0;
                    for (; k + 1 < _sp - 6; k += 2) LineBy(_stack[k], _stack[k + 1]);
                    if (k + 5 < _sp) CurveBy(_stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], _stack[k + 4], _stack[k + 5]);
                    _sp = 0;
                    break;
                }
                case 26:                    // vvcurveto
                {
                    var k = 0;
                    double dx1 = 0;
                    if (_sp % 2 == 1) dx1 = _stack[k++];
                    for (; k + 3 < _sp; k += 4)
                    {
                        CurveBy(dx1, _stack[k], _stack[k + 1], _stack[k + 2], 0, _stack[k + 3]);
                        dx1 = 0;
                    }
                    _sp = 0;
                    break;
                }
                case 27:                    // hhcurveto
                {
                    var k = 0;
                    double dy1 = 0;
                    if (_sp % 2 == 1) dy1 = _stack[k++];
                    for (; k + 3 < _sp; k += 4)
                    {
                        CurveBy(_stack[k], dy1, _stack[k + 1], _stack[k + 2], _stack[k + 3], 0);
                        dy1 = 0;
                    }
                    _sp = 0;
                    break;
                }
                case 30 or 31:              // vhcurveto, hvcurveto
                {
                    var vertical = op == 30;
                    var k = 0;
                    while (k + 3 < _sp)
                    {
                        var last = _sp - k == 5;
                        if (vertical)
                            CurveBy(0, _stack[k], _stack[k + 1], _stack[k + 2], _stack[k + 3], last ? _stack[k + 4] : 0);
                        else
                            CurveBy(_stack[k], 0, _stack[k + 1], _stack[k + 2], last ? _stack[k + 4] : 0, _stack[k + 3]);
                        k += last ? 5 : 4;
                        vertical = !vertical;
                    }
                    _sp = 0;
                    break;
                }
            }
        }

        /// <summary>The mask bytes of hintmask or cntrmask as read (zeros where the charstring ends early).</summary>
        private static byte[] MaskBytes(ReadOnlySpan<byte> code, int at, int count)
        {
            var mask = new byte[count];
            for (var k = 0; k < count; k++) mask[k] = (byte)At(code, at + k);
            return mask;
        }

        private void Escape(int op)
        {
            // CFF2 keeps only the flex operators of the escaped ones (OpenType 8.10.5.1): the others are reserved and clear the stack
            if (font._cff2 && op is not (34 or 35 or 36 or 37))
            {
                _sp = 0;
                return;
            }
            if (op is 34 or 35 or 36 or 37) Emit(1200 + op);
            switch (op)
            {
                case 35:                        // flex
                    if (_sp >= 12)
                    {
                        CurveBy(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                        CurveBy(_stack[6], _stack[7], _stack[8], _stack[9], _stack[10], _stack[11]);
                    }
                    break;
                case 34:                        // hflex
                    if (_sp >= 7)
                    {
                        var y0 = _y;
                        CurveBy(_stack[0], 0, _stack[1], _stack[2], _stack[3], 0);
                        CurveBy(_stack[4], 0, _stack[5], y0 - _y, _stack[6], 0);
                    }
                    break;
                case 36:                        // hflex1
                    if (_sp >= 9)
                    {
                        var y0 = _y;
                        CurveBy(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], 0);
                        CurveBy(_stack[5], 0, _stack[6], _stack[7], _stack[8], y0 - (_y + _stack[7]));
                    }
                    break;
                case 37:                        // flex1
                    if (_sp >= 11)
                    {
                        // d6 is dx6 or dy6 depending on the dominant direction; the other direction returns to the start
                        double dx = 0, dy = 0;
                        for (var k = 0; k < 10; k += 2) { dx += _stack[k]; dy += _stack[k + 1]; }
                        CurveBy(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                        var horizontal = Math.Abs(dx) > Math.Abs(dy);
                        CurveBy(_stack[6], _stack[7], _stack[8], _stack[9], horizontal ? _stack[10] : -dx, horizontal ? -dy : _stack[10]);
                    }
                    break;
                // Arithmetic operators (rare; actually deprecated in Type 2)
                case 9: if (_sp >= 1) _stack[_sp - 1] = Math.Abs(_stack[_sp - 1]); return;
                case 10: if (_sp >= 2) { _stack[_sp - 2] = Fixed(_stack[_sp - 2] + _stack[_sp - 1]); _sp--; } return;
                case 11: if (_sp >= 2) { _stack[_sp - 2] = Fixed(_stack[_sp - 2] - _stack[_sp - 1]); _sp--; } return;
                case 12: if (_sp >= 2) { _stack[_sp - 2] = _stack[_sp - 1] == 0 ? 0 : Fixed(_stack[_sp - 2] / _stack[_sp - 1]); _sp--; } return;
                case 14: if (_sp >= 1) _stack[_sp - 1] = -_stack[_sp - 1]; return;
                case 18: if (_sp >= 1) _sp--; return;
                case 20: if (_sp >= 2) { var idx = (int)_stack[_sp - 1]; if (idx >= 0 && idx < _transient.Length) _transient[idx] = _stack[_sp - 2]; _sp -= 2; } return;
                case 21: if (_sp >= 1) { var idx = (int)_stack[_sp - 1]; _stack[_sp - 1] = idx >= 0 && idx < _transient.Length ? _transient[idx] : 0; } return;
                case 24: if (_sp >= 2) { _stack[_sp - 2] = Fixed(_stack[_sp - 2] * _stack[_sp - 1]); _sp--; } return;
                case 26: if (_sp >= 1) _stack[_sp - 1] = Math.Sqrt(Math.Abs(_stack[_sp - 1])); return;
                case 27: if (_sp >= 1) Push(_stack[_sp - 1]); return;
                case 28: if (_sp >= 2) (_stack[_sp - 1], _stack[_sp - 2]) = (_stack[_sp - 2], _stack[_sp - 1]); return;
                case 0: break;                  // dotsection (deprecated)
            }
            _sp = 0;
        }

        /// <summary>Builds an accented character from two glyphs of the StandardEncoding (like the Type 1 "seac" operator).</summary>
        private void Seac(double adx, double ady, int baseCode, int accentCode, int depth)
        {
            if (depth > 2 || _seacDepth > 2 || baseCode is < 0 or > 255 || accentCode is < 0 or > 255) return;
            var baseGlyph = font.GlyphByName(FontTables.StandardEncoding[baseCode]);
            var accentGlyph = font.GlyphByName(FontTables.StandardEncoding[accentCode]);
            _seacDepth++;   // components that refer back to themselves must not recurse endlessly
            try
            {
                if (baseGlyph > 0) _outline.Append(font.Outline(baseGlyph), Matrix.Identity);
                if (accentGlyph > 0) _outline.Append(font.Outline(accentGlyph), Matrix.Translate(adx, ady));
            }
            finally
            {
                _seacDepth--;
            }
        }

        [ThreadStatic]
        private static int _seacDepth;

        /// <summary>Whether this thread is drawing a component of an accented character.</summary>
        internal static bool InSeac => _seacDepth > 0;

        private double Arg(int k) => k < _sp ? _stack[k] : 0;

        /// <summary>
        /// The result of an arithmetic operator as a 16.16 fixed value, the numbers of Type 2 charstrings (Adobe TN 5177, 4.3): chains of
        /// mul or div grew beyond any finite value and put infinities and NaN into the outline and the FontBBox of the PDF/A copy (No. 1139).
        /// </summary>
        private static double Fixed(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, -32768, 32767.99998);

        private static int At(ReadOnlySpan<byte> code, int i) => i < code.Length ? code[i] : 0;

        private void MoveBy(double dx, double dy)
        {
            _x += dx;
            _y += dy;
            _outline.MoveTo((float)_x, (float)_y);
        }

        private void LineBy(double dx, double dy)
        {
            _x += dx;
            _y += dy;
            _outline.LineTo((float)_x, (float)_y);
        }

        private void CurveBy(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            var x1 = _x + dx1;
            var y1 = _y + dy1;
            var x2 = x1 + dx2;
            var y2 = y1 + dy2;
            _x = x2 + dx3;
            _y = y2 + dy3;
            _outline.CubicTo((float)x1, (float)y1, (float)x2, (float)y2, (float)_x, (float)_y);
        }
    }
}
