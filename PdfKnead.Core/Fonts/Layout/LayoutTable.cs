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

    /// <param name="data">The table data.</param>
    /// <param name="extensionType">Lookup type of extension lookups: 7 in GSUB, 9 in GPOS.</param>
    public LayoutTable(byte[] data, int extensionType)
    {
        T = new TableData(data);
        _extensionType = extensionType;
        _scripts = T.U16(4);
        _features = T.U16(6);
        _lookups = T.U16(8);
        _cache = new Lookup?[LookupCount];
    }

    public TableData T { get; }

    public int LookupCount => _lookups == 0 ? 0 : T.U16(_lookups);

    public Lookup Lookup(int index)
    {
        if (index < 0 || index >= _cache.Length) return new Lookup(0, 0, [], -1);
        if (_cache[index] is { } cached) return cached;
        var at = _lookups + T.U16(_lookups + 2 + index * 2);
        var type = T.U16(at);
        var flag = T.U16(at + 2);
        var count = T.U16(at + 4);
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
                sub += T.U32(sub + 4);
            }
            subtables[i] = sub;
        }
        var filteringSet = (flag & 0x10) != 0 ? T.U16(at + 6 + count * 2) : -1;
        return _cache[index] = new Lookup(type, flag, subtables, filteringSet);
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
        foreach (var index in indices)
        {
            if (index >= featureCount) continue;
            var record = _features + 2 + index * 6;
            if (!features.Contains(T.Tag(record)) && index != requiredIndex) continue;
            var feature = _features + T.U16(record + 4);
            var n = T.U16(feature + 2);
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
        for (var f = 0; f < featureCount; f++)
        {
            var record = _features + 2 + f * 6;
            var tag = T.Tag(record);
            var feature = _features + T.U16(record + 4);
            if (!tags.TryGetValue(tag, out var types)) tags[tag] = types = [];
            for (var k = 0; k < T.U16(feature + 2); k++) types.Add(Lookup(T.U16(feature + 4 + k * 2)).Type);
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
    public bool Skips(Lookup lookup, int glyph)
    {
        var flag = lookup.Flag;
        if ((flag & 0xFF0E) == 0 && lookup.MarkFilteringSet < 0) return false;
        var cls = GlyphClass(glyph);
        if (cls == 1 && (flag & 0x2) != 0) return true;
        if (cls == 2 && (flag & 0x4) != 0) return true;
        if (cls != 3) return false;
        if ((flag & 0x8) != 0) return true;
        if (lookup.MarkFilteringSet >= 0) return !InMarkSet(lookup.MarkFilteringSet, glyph);
        var attach = flag >> 8;
        return attach != 0 && MarkAttachClass(glyph) != attach;
    }
}
