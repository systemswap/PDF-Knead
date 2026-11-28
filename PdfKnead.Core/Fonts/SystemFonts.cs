using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Fonts installed on the computer, read only: for non-embedded CID fonts whose codes are glyph indices of a Windows font
/// (Arial, Calibri … with Identity-H, without ToUnicode). If the font of the same name is installed, the indices apply
/// directly; otherwise <see cref="GlyphOrder"/> translates them into characters for the substitute font.
/// The font directories are only searched on first use, and only the name tables are read.
/// </summary>
internal static class SystemFonts
{
    /// <summary>Maximum total file size of loaded system fonts; beyond it the least recently used font is evicted.</summary>
    private const long MaxLoadedBytes = 64L << 20;
    private static readonly Lazy<FontIndex> Index = new(() => BuildIndex(Directories()), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Dictionary<string, (OpenTypeFont? Font, long LastUse)> Loaded = new(StringComparer.Ordinal);
    private static long _loadedBytes, _clock;

    /// <summary>The index of the installed fonts.</summary>
    /// <param name="Paths">Key of a name (PostScript, full, family with style, named instance) → font file.</param>
    /// <param name="Families">The families offered for new text, in the model regular/bold/italic/bold italic (<see cref="Faces"/>).</param>
    /// <param name="Instances">Key of a named instance of a variable font → its file, weight and slant.</param>
    /// <param name="Faces">Key of a family in the model regular/bold/italic/bold italic (name ID 1) with bold and italic → font file.</param>
    private sealed record FontIndex(Dictionary<string, string> Paths, string[] Families,
        Dictionary<string, (string Path, int Weight, bool Italic)> Instances, Dictionary<(string Family, bool Bold, bool Italic), string> Faces);

    /// <summary>For tests only: no system fonts on this thread (tests the path via <see cref="GlyphOrder"/>).</summary>
    [ThreadStatic] internal static bool DisabledForTests;

    /// <summary>Finds the installed font for a PDF font name ("Arial,Bold", "TimesNewRomanPS-BoldMT", "Verdana").</summary>
    /// <param name="baseFont">The BaseFont name, with or without subset prefix.</param>
    /// <param name="bold">Whether a bold face is wanted in addition to what the name says.</param>
    /// <param name="italic">Whether an italic face is wanted in addition to what the name says.</param>
    /// <returns>The font, or null if it is not installed or cannot be read.</returns>
    public static OpenTypeFont? Find(string baseFont, bool bold, bool italic)
    {
        if (DisabledForTests || PathFor(baseFont, bold, italic) is not { } path) return null;
        return Cached(path);
    }

    /// <summary>
    /// Finds the installed font of a family in the model regular/bold/italic/bold italic (OpenType 57.2): the family is name ID 1 of the font
    /// files, the style their fsSelection (bits 0 and 5, as name ID 2) — „Calibri Light“ is a family of its own with „Calibri Light Italic“ as
    /// its italic, „Arial“ in bold the file of „Arial Bold“. A missing style falls back to the one without italic, then without bold, then to
    /// the regular face (the caller fakes what is missing). Before, the font list offered only the typographic family (name ID 16), and its
    /// other styles (Light, Semibold, Narrow …) could not be chosen (docs/FEHLERGEDAECHTNIS.md, No. 1176).
    /// </summary>
    /// <param name="family">The family name as offered by <see cref="Families"/>.</param>
    /// <param name="bold">Whether the bold face is wanted.</param>
    /// <param name="italic">Whether the italic face is wanted.</param>
    /// <returns>The font; null if no font file has this family name or it cannot be read.</returns>
    internal static OpenTypeFont? FindFace(string family, bool bold, bool italic)
    {
        if (DisabledForTests) return null;
        var key = Key(family);
        if (key.Length == 0) return null;
        var faces = Index.Value.Faces;
        foreach (var (b, i) in (ReadOnlySpan<(bool, bool)>)[(bold, italic), (bold, false), (false, italic), (false, false)])
            if (faces.TryGetValue((key, b, i), out var path)) return Cached(path);
        return null;
    }

    /// <summary>The font of a file, loaded once and kept within <see cref="MaxLoadedBytes"/>.</summary>
    private static OpenTypeFont? Cached(string path)
    {
        lock (Loaded)
        {
            if (Loaded.TryGetValue(path, out var known))
            {
                Loaded[path] = known with { LastUse = ++_clock };
                return known.Font;
            }
        }
        var font = Load(path);
        lock (Loaded)
        {
            if (Loaded.TryGetValue(path, out var other)) return other.Font;   // another thread loaded it first
            var size = font?.FileData.Length ?? 0;
            // Budget with eviction: a fixed maximum number of fonts would find no further font once it is reached, so the rendering
            // would depend on what was opened before. Open documents keep their font through their font object
            while (_loadedBytes + size > MaxLoadedBytes && Loaded.Count > 0)
            {
                var oldest = Loaded.MinBy(e => e.Value.LastUse);
                Loaded.Remove(oldest.Key);
                _loadedBytes -= oldest.Value.Font?.FileData.Length ?? 0;
            }
            Loaded[path] = (font, ++_clock);
            _loadedBytes += size;
            return font;
        }
    }

    /// <summary>For tests only: whether the font is installed (without loading it).</summary>
    internal static bool IsInstalled(string baseFont) => PathFor(baseFont, false, false) is not null;

    /// <summary>
    /// The named instance of an installed variable font that a name designates (through fvar, „Bahnschrift-SemiBold“): its weight and whether
    /// it is italic. <see cref="Find"/> gives the font in its default instance, the only one drawn, so a caller fakes what the default instance
    /// lacks (docs/FEHLERGEDAECHTNIS.md, No. 1159).
    /// </summary>
    /// <param name="baseFont">The font name, with or without subset prefix.</param>
    /// <returns>Weight and italic of the instance; null if the name is no named instance of an installed font.</returns>
    internal static (int Weight, bool Italic)? NamedInstance(string baseFont)
    {
        if (DisabledForTests) return null;
        var key = Key(baseFont);
        var (paths, _, instances, _) = Index.Value;
        return instances.TryGetValue(key, out var instance) && paths.TryGetValue(key, out var path) && path == instance.Path
            ? (instance.Weight, instance.Italic) : null;
    }

    private static string? PathFor(string baseFont, bool bold, bool italic)
    {
        var key = Key(baseFont);
        if (key.Length == 0) return null;
        var index = Index.Value.Paths;
        string? path = null;
        var family = FamilyKey(baseFont);
        var style = (bold || key.Contains("bold"), italic || key.Contains("italic") || key.Contains("oblique")) switch
        {
            (true, true) => "bolditalic",
            (true, false) => "bold",
            (false, true) => "italic",
            _ => "",
        };
        // Exact name, then family with style, then the family alone (regular and bold have the same glyph order)
        foreach (var candidate in (ReadOnlySpan<string>)[key, family + style, family])
            if (index.TryGetValue(candidate, out path)) break;
        return path;
    }

    private static OpenTypeFont? Load(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            return OpenTypeFont.LooksLikeSfnt(data) && OpenTypeFont.Parse(data) is { Broken: false } font ? font : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FontFormatException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Lookup key of a name: lower case, only letters and digits, without subset prefix and without "MT"/"PS". A style after a comma (ISO 32000-2,
    /// 9.6.3 with 9.7.4.1: „Arial,Bold“, also after a PostScript name, „Arial-ItalicMT,Italic“) is appended to the key of the name before it where
    /// that name does not already say it. Before, the whole name was one key („arialitalicmtitalic“), and a PostScript name with a style found
    /// no installed font (docs/FEHLERGEDAECHTNIS.md, No. 1202).
    /// </summary>
    internal static string Key(string name)
    {
        if (name.Length > 7 && name[6] == '+') name = name[7..];
        var comma = name.IndexOf(',');
        if (comma > 0)
        {
            var head = Key(name[..comma]);
            var style = Key(name[(comma + 1)..].Replace(",", ""));
            if (style is "regular" or "normal" or "roman" || head.EndsWith(style, StringComparison.Ordinal)) return head;
            // „Arial-ItalicMT,Bold“: bold before italic, as in the names of the fonts
            return style == "bold" && head.EndsWith("italic", StringComparison.Ordinal) && !head.EndsWith("bolditalic", StringComparison.Ordinal)
                ? head[..^6] + "bolditalic" : head + style;
        }
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        // "TimesNewRomanPS-BoldMT" → "timesnewromanbold", "ArialMT" → "arial"
        var key = sb.ToString();
        if (key.EndsWith("mt", StringComparison.Ordinal)) key = key[..^2];
        key = key.Replace("psbold", "bold").Replace("psitalic", "italic");
        return key.EndsWith("ps", StringComparison.Ordinal) ? key[..^2] : key;
    }

    /// <summary>Family key without style ("arial" for "Arial,BoldItalic" and "Arial-BoldMT").</summary>
    internal static string FamilyKey(string name)
    {
        var key = Key(name);
        foreach (var style in (ReadOnlySpan<string>)["bolditalic", "boldoblique", "italic", "oblique", "bold", "regular"])
            if (key.EndsWith(style, StringComparison.Ordinal) && key.Length > style.Length)
            {
                key = key[..^style.Length];
                break;
            }
        return key;
    }

    // ------------------------------------------------------------ Index

    /// <summary>
    /// Families offered when editing text (see KONZEPT.md, section 5.14): only single font files whose licence allows embedding a
    /// subset (OS/2 fsType), because new text could not use the others. Display names, sorted. The choice offers regular, bold and italic,
    /// so a family is name ID 1 (the model regular/bold/italic/bold italic, OpenType 57.2): every other style of a typographic family
    /// („Calibri Light“, „Arial Narrow“) is a family of its own (<see cref="FindFace"/>; No. 1176).
    /// </summary>
    public static IReadOnlyList<string> Families() => Index.Value.Families;

    /// <summary>The index of the fonts in some directories (the system's, or a test directory).</summary>
    /// <param name="directories">The directories, searched four levels deep.</param>
    /// <returns>Key → font file, and the families offered for new text.</returns>
    internal static (Dictionary<string, string> Paths, string[] Families) Build(IEnumerable<string> directories)
    {
        var (paths, families, _, _) = BuildIndex(directories);
        return (paths, families);
    }

    /// <summary>
    /// The index with the named instances of variable fonts (fvar): their keys find the file of the font after the names of all fonts, so a
    /// font file of the same name goes first (before, a named instance found the font only where its name was the family with bold or italic,
    /// and „Bahnschrift-SemiLightCondensed“ none; No. 1159).
    /// </summary>
    private static FontIndex BuildIndex(IEnumerable<string> directories)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        var instances = new Dictionary<string, (string Path, int Weight, bool Italic)>(StringComparer.Ordinal);
        var faces = new Dictionary<(string Family, bool Bold, bool Italic), string>();
        var families = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in directories)
        {
            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(dir)) continue;
                files = Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                // single fonts .ttf/.otf, collections .ttc/.otc (OpenType 3.2; .otc was passed over before, No. 1160)
                if (!(file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
                      || file.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".otc", StringComparison.OrdinalIgnoreCase))) continue;
                var entry = Entry(file);
                foreach (var name in entry.Keys)
                    if (name.Length > 0) index.TryAdd(name, file);
                foreach (var (key, weight, italic) in entry.Instances) instances.TryAdd(key, (file, weight, italic));
                if (entry.Family is { } family && Key(family) is { Length: > 0 } familyKey) faces.TryAdd((familyKey, entry.Bold, entry.Italic), file);
                if (entry.Usable && entry.Family is { Length: > 0 and < 64 }) families.Add(entry.Family.Trim());
            }
        }
        foreach (var (key, (file, _, _)) in instances) index.TryAdd(key, file);
        return new FontIndex(index, [.. families], instances, faces);
    }

    private static IEnumerable<string> Directories()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
            yield break;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            yield return Path.Combine(home, "Library", "Fonts");
            yield break;
        }
        yield return "/usr/share/fonts";
        yield return "/usr/local/share/fonts";
        yield return Path.Combine(home, ".local", "share", "fonts");
        yield return Path.Combine(home, ".fonts");
    }

    /// <summary>
    /// Keys of a font file (the first font of a collection): PostScript name, full name, family + style; also the display name of the
    /// family (name ID 1, see <see cref="Families"/>) and whether new text can use it (single file, embedding a subset allowed, as in
    /// <c>RichFonts.Embeddable</c>).
    /// </summary>
    private static (List<string> Keys, string? Family, bool Usable) Names(string file)
    {
        var entry = Entry(file);
        return (entry.Keys, entry.Family, entry.Usable);
    }

    /// <summary>What the index notes of a font file.</summary>
    /// <param name="Keys">Keys of its names (<see cref="Names"/>).</param>
    /// <param name="Family">Its family in the model regular/bold/italic/bold italic (<see cref="LegacyFamily"/>).</param>
    /// <param name="Usable">Whether new text can use it.</param>
    /// <param name="Instances">The named instances of a variable font.</param>
    /// <param name="Bold">Its face is bold (fsSelection bit 5 or name ID 2).</param>
    /// <param name="Italic">Its face is italic or oblique (fsSelection bit 0 or 9, or name ID 2).</param>
    private readonly record struct FontEntry(List<string> Keys, string? Family, bool Usable, List<(string Key, int Weight, bool Italic)> Instances,
        bool Bold, bool Italic)
    {
        public static FontEntry None(List<string> keys) => new(keys, null, false, [], false, false);
    }

    /// <summary>The keys, family and usability of a font file (<see cref="Names"/>) with its style and the named instances of a variable font.</summary>
    private static FontEntry Entry(string file)
    {
        var keys = new List<string>(4);
        string? familyName = null;
        var usable = false;
        bool bold = false, italic = false;
        List<(string Key, int Weight, bool Italic)> instances = [];
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> head = stackalloc byte[12];
            if (stream.Read(head) < 12) return FontEntry.None(keys);
            long start = 0;
            var collection = head[..4].SequenceEqual("ttcf"u8);
            if (collection)
            {
                stream.Position = 12;
                if (stream.Read(head[..4]) < 4) return FontEntry.None(keys);
                start = BinaryPrimitives.ReadUInt32BigEndian(head);
                stream.Position = start;
                if (stream.Read(head) < 12) return FontEntry.None(keys);
            }
            int tables = BinaryPrimitives.ReadUInt16BigEndian(head[4..]);
            if (tables > 256) return FontEntry.None(keys);
            var directory = new byte[tables * 16];
            if (stream.Read(directory) < directory.Length) return FontEntry.None(keys);
            var fsType = 0;
            var weight = 400;
            var selection = 0;
            for (var i = 0; i < tables; i++)
            {
                if (!directory.AsSpan(i * 16, 4).SequenceEqual("OS/2"u8)) continue;
                stream.Position = BinaryPrimitives.ReadUInt32BigEndian(directory.AsSpan(i * 16 + 8));
                Span<byte> os2 = stackalloc byte[64];
                var read = stream.ReadAtLeast(os2, os2.Length, throwOnEndOfStream: false);
                if (read >= 10)
                {
                    var version = BinaryPrimitives.ReadUInt16BigEndian(os2);
                    fsType = BinaryPrimitives.ReadUInt16BigEndian(os2[8..]);
                    weight = BinaryPrimitives.ReadUInt16BigEndian(os2[4..]);
                    // versions 0 and 1 know only bits 0–3 (31.2.5; No. 1152)
                    if (version < 2) fsType &= 0x000F;
                    // fsSelection: bit 0 italic, bit 5 bold, bit 9 oblique from version 4 (31.2.20; as RichFonts.StyleOf)
                    if (read == 64 && BinaryPrimitives.ReadUInt32BigEndian(directory.AsSpan(i * 16 + 12)) >= 64)
                        selection = BinaryPrimitives.ReadUInt16BigEndian(os2[62..]) & (version >= 4 ? 0x221 : 0x021);
                }
                break;
            }
            // Restricted licence (this computer only), no subsetting, bitmap embedding only: cannot be embedded
            usable = !collection && (fsType & 0x000F) != 0x0002 && (fsType & 0x0300) == 0;
            for (var i = 0; i < tables; i++)
            {
                if (!directory.AsSpan(i * 16, 4).SequenceEqual("name"u8)) continue;
                var offset = BinaryPrimitives.ReadUInt32BigEndian(directory.AsSpan(i * 16 + 8));
                var length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(directory.AsSpan(i * 16 + 12)), 1 << 20);
                var name = new byte[length];
                stream.Position = offset;
                if (stream.Read(name) < length) return FontEntry.None(keys);
                string? ps = Read(name, 6), full = Read(name, 4), family = Read(name, 16) ?? Read(name, 1), style = Read(name, 17) ?? Read(name, 2);
                if (ps is not null) keys.Add(Key(ps));
                if (full is not null) keys.Add(Key(full));
                if (family is not null)
                {
                    var f = Key(family);
                    var s = style is null ? "" : Key(style);
                    keys.Add(s is "regular" or "normal" or "roman" or "" ? f : f + s);
                }
                familyName = LegacyFamily(name);
                var subfamily = Read(name, 2) is { } id2 ? Key(id2) : "";
                bold = (selection & 0x020) != 0 || subfamily is "bold" or "bolditalic" or "boldoblique";
                italic = (selection & 0x201) != 0 || subfamily is "italic" or "oblique" or "bolditalic" or "boldoblique";
                if (FvarOf(stream, directory, tables) is { } fvar) instances = NamedInstances(fvar, name, family, weight);
                break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return FontEntry.None(keys);
        }
        return new FontEntry(keys, familyName, usable, instances, bold, italic);
    }

    /// <summary>
    /// The family of a font in the model regular/bold/italic/bold italic (OpenType 57.2, 30.4): name ID 1. Without it, the typographic family
    /// (ID 16) with the parts of the typographic subfamily (ID 17) other than Regular, Bold, Italic and Oblique moved into it, as the
    /// specification describes the projection („Selawik“ with „Condensed Bold“ → „Selawik Condensed“); without both, none.
    /// </summary>
    private static string? LegacyFamily(byte[] name)
    {
        if (Read(name, 1) is { } family) return family;
        if (Read(name, 16) is not { } typographic) return null;
        var extra = (Read(name, 17) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !(w.Equals("Regular", StringComparison.OrdinalIgnoreCase) || w.Equals("Bold", StringComparison.OrdinalIgnoreCase)
                          || w.Equals("Italic", StringComparison.OrdinalIgnoreCase) || w.Equals("Oblique", StringComparison.OrdinalIgnoreCase)));
        return string.Join(' ', [typographic, .. extra]);
    }

    /// <summary>Named instances read per font file.</summary>
    private const int MaxInstances = 256;

    /// <summary>The fvar table of the font whose directory is given (at most 64 KB read); null without one.</summary>
    private static byte[]? FvarOf(FileStream stream, byte[] directory, int tables)
    {
        for (var i = 0; i < tables; i++)
        {
            if (!directory.AsSpan(i * 16, 4).SequenceEqual("fvar"u8)) continue;
            var length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(directory.AsSpan(i * 16 + 12)), 1 << 16);
            var fvar = new byte[length];
            stream.Position = BinaryPrimitives.ReadUInt32BigEndian(directory.AsSpan(i * 16 + 8));
            return stream.Read(fvar) == length ? fvar : null;
        }
        return null;
    }

    /// <summary>
    /// The named instances of a variable font (OpenType fvar 16.1.3): per instance the keys of its names — family (ID 16, else 1) or the prefix
    /// of the variations' PostScript names (ID 25) with the subfamily name of the instance, and its own PostScript name (postScriptNameID 6 or
    /// 256–32767) — with the weight of its wght coordinate (else the font's) and whether it is italic (ital ≥ 0.5 or slnt ≠ 0). The coordinates
    /// are user values in Fixed 16.16. Records outside the table or smaller than their content are passed over (fvar 16.1.1).
    /// </summary>
    private static List<(string Key, int Weight, bool Italic)> NamedInstances(byte[] fvar, byte[] name, string? family, int defaultWeight)
    {
        var result = new List<(string, int, bool)>();
        if (fvar.Length < 16 || BinaryPrimitives.ReadUInt16BigEndian(fvar) != 1) return result;
        int axesAt = BinaryPrimitives.ReadUInt16BigEndian(fvar.AsSpan(4)), axisCount = BinaryPrimitives.ReadUInt16BigEndian(fvar.AsSpan(8)),
            axisSize = BinaryPrimitives.ReadUInt16BigEndian(fvar.AsSpan(10)), instanceCount = BinaryPrimitives.ReadUInt16BigEndian(fvar.AsSpan(12)),
            instanceSize = BinaryPrimitives.ReadUInt16BigEndian(fvar.AsSpan(14));
        if (axesAt == 0 || axisCount == 0 || axisSize < 20 || instanceSize < axisCount * 4 + 4 || axesAt + (long)axisCount * axisSize > fvar.Length)
            return result;
        int wght = -1, ital = -1, slnt = -1;
        for (var a = 0; a < axisCount; a++)
        {
            var tag = fvar.AsSpan(axesAt + a * axisSize, 4);
            if (tag.SequenceEqual("wght"u8)) wght = a;
            else if (tag.SequenceEqual("ital"u8)) ital = a;
            else if (tag.SequenceEqual("slnt"u8)) slnt = a;
        }
        var prefix = Read(name, 25);
        var instancesAt = axesAt + axisCount * axisSize;
        // at most MaxInstances: each looks up up to three names in a name table of up to 1 MB (fonts have a few dozen)
        for (var i = 0; i < Math.Min(instanceCount, MaxInstances) && instancesAt + (long)(i + 1) * instanceSize <= fvar.Length; i++)
        {
            var at = instancesAt + i * instanceSize;
            double Coordinate(int axis) => BinaryPrimitives.ReadInt32BigEndian(fvar.AsSpan(at + 4 + axis * 4)) / 65536.0;
            var weight = wght >= 0 ? (int)Math.Clamp(Math.Round(Coordinate(wght)), 1, 1000) : defaultWeight;
            var italic = ital >= 0 && Coordinate(ital) >= 0.5 || slnt >= 0 && Coordinate(slnt) != 0;
            var keys = new List<string>(3);
            if (Read(name, BinaryPrimitives.ReadUInt16BigEndian(fvar.AsSpan(at))) is { } subfamily)
            {
                var s = Key(subfamily);
                foreach (var f in (ReadOnlySpan<string?>)[family, prefix])
                    if (f is not null) keys.Add(s is "regular" or "normal" or "roman" ? Key(f) : Key(f) + s);
            }
            if (instanceSize >= axisCount * 4 + 6 && BinaryPrimitives.ReadUInt16BigEndian(fvar.AsSpan(at + 4 + axisCount * 4)) is var psId &&
                psId is 6 or >= 256 and <= 32767 && Read(name, psId) is { } ps)
                keys.Add(Key(ps));
            foreach (var key in keys)
                if (key.Length > 0) result.Add((key, weight, italic));
        }
        return result;
    }

    /// <summary>
    /// Reads entry <paramref name="id"/> of the name table: Windows Unicode (encodings 0 and 1) preferred, otherwise Mac OS Roman, English
    /// first — the rule of the font reader (<see cref="NameRecords"/>; No. 1160).
    /// </summary>
    private static string? Read(byte[] name, int id) => NameRecords.Read(name, 0, id, systemIndex: true);
}

/// <summary>
/// Glyph order of common Windows fonts (glyph → Unicode), like the tables common PDF readers ship for non-embedded CID fonts with
/// glyph indices: without the font on the computer, the indices become characters of the substitute font. Produced by
/// <c>pdf-knead-messen --glyphenfolge-schreiben</c> from the fonts of Windows 11 (Fonts/Data/glyphorder.bin).
/// </summary>
internal static class GlyphOrder
{
    private static readonly Lazy<Dictionary<string, ushort[]>> Tables = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Returns the table of the font family (glyph index → Unicode), or null if the family is unknown.</summary>
    public static ushort[]? For(string baseFont) => Tables.Value.GetValueOrDefault(SystemFonts.FamilyKey(baseFont));

    private static Dictionary<string, ushort[]> Load()
    {
        var tables = new Dictionary<string, ushort[]>(StringComparer.Ordinal);
        using var resource = typeof(GlyphOrder).Assembly.GetManifestResourceStream("Fonts/glyphorder.bin");
        if (resource is null) return tables;
        using var deflate = new DeflateStream(resource, CompressionMode.Decompress);
        using var data = new MemoryStream();
        deflate.CopyTo(data);
        var d = data.ToArray();
        var at = 1;
        for (var f = 0; f < d[0] && at < d.Length; f++)
        {
            var name = Encoding.ASCII.GetString(d, at + 1, d[at]);
            at += 1 + d[at];
            int count = d[at] << 8 | d[at + 1];
            at += 2;
            var table = new ushort[count];
            for (var g = 0; g < count; g++, at += 2) table[g] = (ushort)(d[at] << 8 | d[at + 1]);
            tables[name] = table;
        }
        return tables;
    }
}
