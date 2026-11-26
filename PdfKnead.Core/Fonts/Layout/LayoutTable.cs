namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// Bounds-checked reads from a font table: reads outside the data return 0. Damaged or truncated
/// tables therefore break nothing; they merely have no effect.
/// </summary>
internal readonly struct TableData(byte[] data)
{
    public byte[] Bytes => data;

    public int Length => data.Length;

    public int U16(int at) => at >= 0 && at <= data.Length - 2 ? (data[at] << 8) | data[at + 1] : 0;

    public short I16(int at) => (short)U16(at);

    public int U32(int at) => at >= 0 && at <= data.Length - 4
        ? (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3]
        : 0;

    public string Tag(int at) => at >= 0 && at <= data.Length - 4
        ? string.Create(4, (data, at), static (span, s) =>
        {
            for (var i = 0; i < 4; i++) span[i] = (char)s.data[s.at + i];
        })
        : "    ";

    /// <summary>Position of a glyph in a Coverage table (glyph → coverage index); -1 = not covered.</summary>
    public int Coverage(int offset, int glyph)
    {
        switch (U16(offset))
        {
            case 1:
            {
                // Sorted list: binary search
                int lo = 0, hi = U16(offset + 2) - 1;
                while (lo <= hi)
                {
                    var mid = (lo + hi) >> 1;
                    var g = U16(offset + 4 + mid * 2);
                    if (g == glyph) return mid;
                    if (g < glyph) lo = mid + 1;
                    else hi = mid - 1;
                }
                return -1;
            }
            case 2:
            {
                int lo = 0, hi = U16(offset + 2) - 1;
                while (lo <= hi)
                {
                    var mid = (lo + hi) >> 1;
                    var at = offset + 4 + mid * 6;
                    if (glyph < U16(at)) hi = mid - 1;
                    else if (glyph > U16(at + 2)) lo = mid + 1;
                    else return U16(at + 4) + glyph - U16(at);
                }
                return -1;
            }
            default:
                return -1;
        }
    }

    /// <summary>Class of a glyph in a ClassDef table (0 = none).</summary>
    public int Class(int offset, int glyph)
    {
        switch (U16(offset))
        {
            case 1:
            {
                var start = U16(offset + 2);
                var count = U16(offset + 4);
                return glyph >= start && glyph < start + count ? U16(offset + 6 + (glyph - start) * 2) : 0;
            }
            case 2:
            {
                int lo = 0, hi = U16(offset + 2) - 1;
                while (lo <= hi)
                {
                    var mid = (lo + hi) >> 1;
                    var at = offset + 4 + mid * 6;
                    if (glyph < U16(at)) hi = mid - 1;
                    else if (glyph > U16(at + 2)) lo = mid + 1;
                    else return U16(at + 4);
                }
                return 0;
            }
            default:
                return 0;
        }
    }
}

/// <summary>One lookup: type, flags and its subtables (absolute positions, extension subtables resolved).</summary>
internal sealed record Lookup(int Type, int Flag, int[] Subtables, int MarkFilteringSet);

/// <summary>
/// Common structure of GSUB and GPOS (OpenType "Common Table Formats"): scripts with their
/// language systems, features ("liga", "kern" …) and the lookups that implement them.
/// </summary>
internal sealed class LayoutTable
{
    private readonly int _scripts;
    private readonly int _features;
    private readonly int _lookups;
    private readonly int _extensionType;
    private readonly Lookup?[] _cache;

    /// <summary>The lookups read, by the position of their table (several LookupList entries can name one table).</summary>
    private readonly Dictionary<int, Lookup> _byOffset = [];

    /// <summary>Subtable offsets read so far, against <see cref="TableData.Length"/> / 2.</summary>
    private long _subtablesRead;

    /// <summary>Feature tables replaced for the default instance (feature index → absolute offset of the alternate table).</summary>
    private readonly Dictionary<int, int> _substitutions = [];

    /// <param name="data">The table data.</param>
    /// <param name="extensionType">Lookup type of extension lookups: 7 in GSUB, 9 in GPOS.</param>
    /// <param name="axisCount">The axes of a variable font (fvar), 0 for another font: only in a variable font its FeatureVariations count.</param>
    public LayoutTable(byte[] data, int extensionType, int axisCount = 0)
    {
        T = new TableData(data);
        _extensionType = extensionType;
        _scripts = T.U16(4);
        _features = T.U16(6);
        _lookups = T.U16(8);
        _cache = new Lookup?[LookupCount];
        _axisCount = axisCount;
        if (axisCount > 0 && T.U16(2) >= 1) ReadFeatureVariations((uint)T.U32(10));
    }

    private readonly int _axisCount;

    /// <summary>
    /// FeatureVariations (OpenType 20.3, 21.2; version 1.1): the first record whose conditions the default instance meets — the instance
    /// drawn — replaces the feature tables it names. Before not read, and a font whose variations include the default instance shaped with
    /// the wrong tables (docs/FEHLERGEDAECHTNIS.md, No. 1154). In the default instance every normalized axis coordinate is 0: a range
    /// condition (format 1) holds if its range includes 0; a condition of another format does not hold, and an empty set always does.
    /// </summary>
    private void ReadFeatureVariations(uint offset)
    {
        if (offset == 0 || offset > T.Length - 8) return;
        var table = (int)offset;
        // major version 1 only (OpenType 2.10.1; No. 1155)
        if (T.U16(table) != 1) return;
        var count = Math.Min((uint)T.U32(table + 4), (uint)(T.Length - table - 8) / 8);
        for (var i = 0; i < count; i++)
        {
            var record = table + 8 + i * 8;
            var conditions = (uint)T.U32(record);
            var substitution = (uint)T.U32(record + 4);
            if (conditions != 0 && !DefaultInstanceMeets(table + (long)conditions)) continue;
            if (substitution == 0 || table + (long)substitution > T.Length - 6) return;
            var at = table + (int)substitution;
            // a substitution table of a version this reader does not know: its record is passed over (No. 1155; before the search ended)
            if (T.U16(at) != 1) continue;
            var n = Math.Min(T.U16(at + 4), (T.Length - at - 6) / 6);
            for (var k = 0; k < n; k++)
            {
                var feature = T.U16(at + 6 + k * 6);
                var alternate = at + (long)(uint)T.U32(at + 8 + k * 6);
                if (alternate + 4 <= T.Length) _substitutions.TryAdd(feature, (int)alternate);
            }
            return;
        }
    }

    private bool DefaultInstanceMeets(long set)
    {
        if (set + 2 > T.Length) return false;
        var at = (int)set;
        var count = Math.Min(T.U16(at), (T.Length - at - 2) / 4);
        for (var i = 0; i < count; i++)
        {
            var condition = at + (long)(uint)T.U32(at + 2 + i * 4);
            if (condition + 8 > T.Length) return false;
            var c = (int)condition;
            // format 1: axis index, minimum and maximum (F2DOT14) — the default instance lies at 0; an axis the font does not have holds no
            // condition (2.10.4; No. 1155)
            if (T.U16(c) != 1 || T.U16(c + 2) >= _axisCount || T.I16(c + 4) > 0 || T.I16(c + 6) < 0) return false;
        }
        return true;
    }

    /// <summary>The feature table of a feature record: the alternate one for the default instance, else its own.</summary>
    private int FeatureTable(int index, int record) => _substitutions.TryGetValue(index, out var alternate) ? alternate : _features + T.U16(record + 4);

    public TableData T { get; }

    public int LookupCount => _lookups == 0 ? 0 : T.U16(_lookups);

    public Lookup Lookup(int index)
    {
        if (index < 0 || index >= _cache.Length) return new Lookup(0, 0, [], -1);
        if (_cache[index] is { } cached) return cached;
        // the shaper of a font is shared between threads
        lock (_byOffset) return _cache[index] = Read(_lookups + T.U16(_lookups + 2 + index * 2));
    }

    /// <summary>
    /// Reads the lookup table at <paramref name="at"/> once. Its subtable offsets must lie in the table, and in a well-formed table the
    /// offset arrays of different lookups do not overlap, so all lookups together name at most a subtable per two bytes of the table: a
    /// lookup beyond that is read as empty. Before, each of 65 535 LookupList entries kept its own array of the 65 535 subtables an
    /// overlapping table claimed for it, 16 GB in all (No. 1137).
    /// </summary>
    private Lookup Read(int at)
    {
        if (_byOffset.TryGetValue(at, out var shared)) return shared;
        var type = T.U16(at);
        var flag = T.U16(at + 2);
        var declared = T.U16(at + 4);
        var count = Math.Min(declared, Math.Max(0, (T.Length - at - 6) / 2));
        if (_subtablesRead + count > T.Length / 2) count = 0;
        _subtablesRead += count;
        var subtables = new int[count];
        // Remembered before the loop: the type is replaced by the real one at the first entry, but all
        // subtables are extensions and each of them must be resolved, not only the first
        var extension = type == _extensionType;
        for (var i = 0; i < count; i++)
        {
            var sub = at + T.U16(at + 6 + i * 2);
            if (extension)
            {
                // Extension: real lookup type and 32-bit offset
                if (i == 0) type = T.U16(sub + 2);
                // unsigned and inside the table: an offset of 2 GB or more was added as a negative number and pointed before the subtable
                // (No. 1150); beyond the table it is no subtable
                var offset = (uint)T.U32(sub + 4);
                sub = sub < T.Length && offset < (uint)(T.Length - sub) ? sub + (int)offset : T.Length;
            }
            subtables[i] = sub;
        }
        var filteringSet = (flag & 0x10) != 0 ? T.U16(at + 6 + declared * 2) : -1;
        return _byOffset[at] = new Lookup(type, flag, subtables, filteringSet);
    }

    /// <summary>Language system (LangSys) for a script: the requested script, otherwise "DFLT", otherwise the first one.</summary>
    private int LangSys(string script)
    {
        if (_scripts == 0) return -1;
        var count = T.U16(_scripts);
        var chosen = -1;
        for (var pass = 0; pass < 3 && chosen < 0; pass++)
            for (var i = 0; i < count; i++)
            {
                var tag = T.Tag(_scripts + 2 + i * 6);
                if (pass == 0 && tag != script || pass == 1 && tag != "DFLT") continue;
                chosen = _scripts + T.U16(_scripts + 2 + i * 6 + 4);
                break;
            }
        if (chosen < 0) return -1;
        var defaultLang = T.U16(chosen);
        if (defaultLang != 0) return chosen + defaultLang;
        // Without a default language system: the first one
        return T.U16(chosen + 2) > 0 ? chosen + T.U16(chosen + 4 + 4) : -1;
    }

    /// <summary>Lookups of the requested features (plus the required feature), in LookupList order, which is the order in which they are applied.</summary>
    /// <param name="features">The feature tags.</param>
    /// <param name="script">The script tag (its language system, else DFLT, else the first).</param>
    /// <param name="required">Whether the required feature comes along (once per shaping: false for the later stages of Arabic, No. 910).</param>
    public int[] LookupsFor(IReadOnlyCollection<string> features, string script = "latn", bool required = true)
    {
        var lookups = new SortedSet<int>();
        if (_features == 0 || LangSys(script) is var lang && lang < 0) return [];
        var requiredIndex = required ? T.U16(lang + 2) : 0xFFFF;
        var count = T.U16(lang + 4);
        var indices = new List<int>(count + 1);
        if (requiredIndex != 0xFFFF) indices.Add(requiredIndex);
        for (var i = 0; i < count; i++) indices.Add(T.U16(lang + 6 + i * 2));
        var featureCount = T.U16(_features);
        // Each feature table once, and together at most as many lookup indices as the table has two-byte places: a language system
        // naming one feature 65 535 times, or 65 535 records naming overlapping tables of 65 535 indices, made billions of additions
        // (No. 1137)
        var read = new HashSet<int>();
        var budget = (long)T.Length / 2;
        foreach (var index in indices)
        {
            if (index >= featureCount) continue;
            var record = _features + 2 + index * 6;
            if (!features.Contains(T.Tag(record)) && index != requiredIndex) continue;
            var feature = FeatureTable(index, record);
            if (!read.Add(feature)) continue;
            var n = Math.Min(T.U16(feature + 2), Math.Max(0, (T.Length - feature - 4) / 2));
            if ((budget -= n) < 0) break;
            for (var k = 0; k < n; k++) lookups.Add(T.U16(feature + 4 + k * 2));
        }
        return [.. lookups];
    }

    /// <summary>For the command line: scripts, features and lookup types.</summary>
    public IEnumerable<string> Describe()
    {
        if (_scripts == 0) yield break;
        var featureCount = _features == 0 ? 0 : T.U16(_features);
        var scriptCount = T.U16(_scripts);
        var tags = new SortedDictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        var read = new HashSet<(string, int)>();
        var budget = (long)T.Length / 2;
        for (var f = 0; f < featureCount; f++)
        {
            var record = _features + 2 + f * 6;
            var tag = T.Tag(record);
            var feature = FeatureTable(f, record);
            if (!tags.TryGetValue(tag, out var types)) tags[tag] = types = [];
            // bounded like LookupsFor (No. 1137)
            if (!read.Add((tag, feature))) continue;
            var n = Math.Min(T.U16(feature + 2), Math.Max(0, (T.Length - feature - 4) / 2));
            if ((budget -= n) < 0) break;
            for (var k = 0; k < n; k++) types.Add(Lookup(T.U16(feature + 4 + k * 2)).Type);
        }
        var scripts = new List<string>();
        for (var i = 0; i < scriptCount; i++) scripts.Add(T.Tag(_scripts + 2 + i * 6));
        yield return $"Schriftsysteme: {string.Join(", ", scripts)}; {LookupCount} Lookups";
        foreach (var (tag, types) in tags) yield return $"  {tag}: Art {string.Join(", ", types)}";
    }
}

/// <summary>
/// GDEF: glyph classes (1 base, 2 ligature, 3 mark, 4 component) and mark classes and sets, so that
/// lookups can skip marks or ligatures.
/// </summary>
internal sealed class GlyphDefinitions(byte[] data)
{
    private readonly TableData _t = new(data);

    /// <summary>Whether the table has a GlyphClassDef; without one the shaper derives the classes itself (OpenType 18.1).</summary>
    public bool HasGlyphClasses => _t.U16(4) > 0;

    public int GlyphClass(int glyph) => _t.U16(4) is var offset and > 0 ? _t.Class(offset, glyph) : 0;

    public int MarkAttachClass(int glyph) => _t.U16(10) is var offset and > 0 ? _t.Class(offset, glyph) : 0;

    /// <summary>Whether a mark belongs to the mark glyph set <paramref name="set"/> (GDEF 1.2).</summary>
    public bool InMarkSet(int set, int glyph)
    {
        if (_t.U16(2) < 2 || _t.U16(12) is not (var defs and > 0)) return false;
        if (set >= _t.U16(defs + 2)) return false;
        return _t.Coverage(defs + _t.U32(defs + 4 + set * 4), glyph) >= 0;
    }

    /// <summary>Whether a lookup with these flags skips the glyph.</summary>
    public bool Skips(Lookup lookup, int glyph) => Skips(this, lookup, glyph, GlyphClass(glyph));

    /// <summary>
    /// Whether a lookup with these flags skips a glyph of class <paramref name="cls"/> (from GlyphClassDef, or derived by the shaper when the font
    /// has none). Mark attachment classes and mark glyph sets come from <paramref name="gdef"/>; without it a mark is in no class and no set.
    /// </summary>
    public static bool Skips(GlyphDefinitions? gdef, Lookup lookup, int glyph, int cls)
    {
        var flag = lookup.Flag;
        if ((flag & 0xFF0E) == 0 && lookup.MarkFilteringSet < 0) return false;
        if (cls == 1 && (flag & 0x2) != 0) return true;
        if (cls == 2 && (flag & 0x4) != 0) return true;
        if (cls != 3) return false;
        if ((flag & 0x8) != 0) return true;
        if (lookup.MarkFilteringSet >= 0) return gdef is null || !gdef.InMarkSet(lookup.MarkFilteringSet, glyph);
        var attach = flag >> 8;
        return attach != 0 && (gdef?.MarkAttachClass(glyph) ?? 0) != attach;
    }

    /// <summary>
    /// The caret values of a ligature in the LigCaretList (OpenType 18.2.4): its LigGlyph table must hold exactly <paramref name="count"/> carets
    /// (one fewer than its components), each of format 1 or 3 (a coordinate; the device or variation data of format 3 are not used — the
    /// default instance at no particular resolution) or 2 (a contour point, returned as its index). Null for a glyph without carets, another
    /// number of them, a NULL offset or an unknown format. The count asked for bounds what is read.
    /// </summary>
    public (int Format, int Value)[]? LigatureCarets(int glyph, int count)
    {
        var list = _t.U16(8);
        if (list == 0 || count <= 0 || _t.U16(list) == 0) return null;
        var index = _t.Coverage(list + _t.U16(list), glyph);
        if (index < 0 || index >= _t.U16(list + 2) || _t.U16(list + 4 + index * 2) is not (var offset and > 0)) return null;
        var ligGlyph = list + offset;
        if (_t.U16(ligGlyph) != count) return null;
        var carets = new (int Format, int Value)[count];
        for (var k = 0; k < count; k++)
        {
            if (_t.U16(ligGlyph + 2 + k * 2) is not (var at and > 0)) return null;
            var format = _t.U16(ligGlyph + at);
            if (format is not (1 or 2 or 3)) return null;
            carets[k] = (format, format == 2 ? _t.U16(ligGlyph + at + 2) : _t.I16(ligGlyph + at + 2));
        }
        return carets;
    }
}
