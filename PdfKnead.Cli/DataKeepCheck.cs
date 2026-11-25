using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Cli;

/// <summary>
/// "pdf-knead speichertest … --datenerhalt [--version v] [--arlington &lt;tsv folder&gt;]": the data-keeping round trip (docs/ARBEITSWEISE.md,
/// principle 14 c; docs/normabgleich/PLAN.md §6 point 8 b). Every file is saved as it is (no change asked for) and walked side by side with its
/// copy — the catalogue (without the page tree), every page (with what it inherits), the document information.
/// <para>What PDF-Knead does not know must stand in the copy unchanged: a key that ISO 32000 does not define (taken from the tables of the
/// Arlington file-format model; the keys of vendor extensions such as AAPL: or CIP4_ count as foreign), everything below such a key and
/// below /Private (the private data of /PieceInfo), and every property of an XMP packet except those the writer updates (FG 613). A foreign
/// value missing or different is an error.</para>
/// <para>A key of the standard that differs needs a named reason, or it is listed as unexplained (also an error): a rule — the default value
/// (Arlington), an empty container, the version written (a key Arlington has only from a later version, or deprecated in every table by the
/// version written), the resources the page does not use (FG 788) — or a kind of the writer's own decisions with its entry in the error log
/// (<see cref="Decisions"/>). Trees (names, numbers) are compared by their entries, pages and widgets with what they inherit, fields by
/// their full name.</para>
/// </summary>
internal static class DataKeepCheck
{
    /// <summary>Keys whose dictionary values are maps: their keys are names given by the file, not keys of the standard.</summary>
    private static readonly HashSet<string> Maps = new(StringComparer.Ordinal)
    {
        "Font", "XObject", "ExtGState", "ColorSpace", "Pattern", "Shading", "Properties", "ClassMap", "RoleMap", "Dests", "CharProcs",
        "PieceInfo", "Extensions", "Schema", "Colorants", "N", "D", "R", "Encoding",
    };

    /// <summary>Keys of the name dictionary (ISO 32000-2, 7.7.4, Table 32) whose values are name trees.</summary>
    private static readonly HashSet<string> NameTrees = new(StringComparer.Ordinal)
    {
        "Dests", "AP", "JavaScript", "Pages", "Templates", "IDS", "URLS", "EmbeddedFiles", "AlternatePresentations", "Renditions",
    };

    /// <summary>Keys that only describe how a stream is stored: the writer writes them anew.</summary>
    private static readonly HashSet<string> Storage = new(StringComparer.Ordinal) { "Length", "Filter", "DecodeParms", "DP", "DL" };

    /// <summary>Attributes of a field a widget inherits (ISO 32000-2, 12.7.4.1 and 12.7.4.3, Tables 226, 228, 231).</summary>
    private static readonly string[] FieldInherited = ["FT", "Ff", "V", "DV", "DA", "Q", "DS", "RV", "MaxLen"];

    /// <summary>
    /// The writer's own decisions about keys of the standard, by kind (the key and what happened, the start of the path), each with the entry
    /// in docs/FEHLERGEDAECHTNIS.md that decided it.
    /// </summary>
    /// <summary>The entries of a font descriptor (ISO 32000-2, Tables 120–122).</summary>
    private static readonly HashSet<string> FontDescriptorKeys =
    [
        "Type", "FontName", "FontFamily", "FontStretch", "FontWeight", "Flags", "FontBBox", "ItalicAngle", "Ascent", "Descent", "Leading",
        "CapHeight", "XHeight", "StemV", "StemH", "AvgWidth", "MaxWidth", "MissingWidth", "FontFile", "FontFile2", "FontFile3", "CharSet",
        "Style", "Lang", "FD", "CIDSet",
    ];

    private static readonly (string Kind, string Path, string Reason)[] Decisions =
    [
        ("/Producer Wert anders", "Info", "eigenes /Producer und /ModDate beim Speichern (FG 613, 810)"),
        ("/ModDate Wert anders", "Info", "eigenes /Producer und /ModDate beim Speichern (FG 613, 810)"),
        ("/ModDate fehlt", "Info", "2.0: /Info nur mit dem, was die Quelle hatte (FG 702, 810)"),
        ("/Producer fehlt", "Info", "2.0: kein eigenes /Producer im Info-Wörterbuch (FG 810)"),
        ("/Title Wert anders", "Info", "Info und XMP abgeglichen, das jüngere gilt (FG 613)"),
        ("/Author Wert anders", "Info", "Info und XMP abgeglichen, das jüngere gilt (FG 613)"),
        ("/Subject Wert anders", "Info", "Info und XMP abgeglichen, das jüngere gilt (FG 613)"),
        ("/Keywords Wert anders", "Info", "Info und XMP abgeglichen, das jüngere gilt (FG 613)"),
        ("/Creator Wert anders", "Info", "Info und XMP abgeglichen, das jüngere gilt (FG 613)"),
        ("/Type Wert anders", "Katalog", "der Katalog heißt /Type /Catalog (Pflicht, ISO 32000-2, Tabelle 29)"),
        ("/Version fehlt", "Katalog", "/Version nur, wo der Inhalt mehr als die Kopfzeile braucht (FG 553)"),
        ("/Perms fehlt", "", "neu geschriebene Datei: Nutzungsrechte und Änderungsschutz der Signatur gelten nicht mehr (FG 539)"),
        ("/Widths Typ anders", "/Font/", "einfache Schrift: /Widths war keine Breitentabelle — die Breiten der Anzeige eingesetzt (FG 886)"),
        ("/Encode Wert anders", "", "Typ-3-Funktion mit Grenze am Rand: in 1.x um 1/100 000 nach innen, Encode der Nachbarn angepasst (FG 801)"),
        ("/Bounds Wert anders", "", "Typ-3-Funktion mit Grenze am Rand: in 1.x um 1/100 000 nach innen, Encode der Nachbarn angepasst (FG 801)"),
        ("/Bounds Länge anders", "", "Typ-3-Funktion: eine Grenze jenseits von Domain1 gehört zu einer Funktion, die nie erreicht wird (FG 801)"),
        ("/Bounds Einträge fehlen", "", "Typ-3-Funktion: eine Grenze jenseits von Domain1 gehört zu einer Funktion, die nie erreicht wird (FG 801)"),
        ("/Encode Länge anders", "", "Typ-3-Funktion: eine Grenze jenseits von Domain1 gehört zu einer Funktion, die nie erreicht wird (FG 801)"),
        ("/Encode Einträge fehlen", "", "Typ-3-Funktion: eine Grenze jenseits von Domain1 gehört zu einer Funktion, die nie erreicht wird (FG 801)"),
        ("/Functions Länge anders", "", "Typ-3-Funktion: eine Grenze jenseits von Domain1 gehört zu einer Funktion, die nie erreicht wird (FG 801)"),
        ("/Functions Einträge fehlen", "", "Typ-3-Funktion: eine Grenze jenseits von Domain1 gehört zu einer Funktion, die nie erreicht wird (FG 801)"),
    ];

    /// <summary>At most this many objects are compared per file (a guard against huge files; reported).</summary>
    private const int MaxObjects = 400_000;

    /// <summary>XMP properties the writer updates on every save (FG 613), as namespace + local name.</summary>
    private static readonly HashSet<string> XmpUpdated = new(StringComparer.Ordinal)
    {
        "http://ns.adobe.com/xap/1.0/ModifyDate", "http://ns.adobe.com/xap/1.0/MetadataDate", "http://ns.adobe.com/pdf/1.3/Producer",
        "http://ns.adobe.com/xap/1.0/mm/InstanceID",
    };

    /// <summary>XMP properties the writer reconciles with /Info (FG 613: the younger side wins).</summary>
    private static readonly HashSet<string> XmpReconciled = new(StringComparer.Ordinal)
    {
        "http://purl.org/dc/elements/1.1/title", "http://purl.org/dc/elements/1.1/description", "http://purl.org/dc/elements/1.1/creator",
        "http://ns.adobe.com/pdf/1.3/Keywords", "http://ns.adobe.com/xap/1.0/CreatorTool",
    };

    /// <summary>
    /// The keys of the standard from the Arlington tables, with defaults and versions per table; a dictionary is matched to its tables by /Type
    /// and /Subtype (the values a table allows for them), so that a key deprecated for one kind of object (/Name of a font) is not taken as
    /// deprecated for another (/Name of a stamp).
    /// </summary>
    private sealed class Model
    {
        public readonly HashSet<string> Keys = new(StringComparer.Ordinal) { "Kids", "Names", "Nums", "Limits" };   // trees (7.9.6, 7.9.7)
        public readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal);
        private readonly List<Table> _tables = [];

        /// <summary>Per key: the tables its value can be (the column Link of every table with the key).</summary>
        private readonly Dictionary<string, HashSet<string>> _links = new(StringComparer.Ordinal);

        private sealed record Table(string Name, HashSet<string>? Types, HashSet<string>? Subtypes, Dictionary<string, (string Since, string? Deprecated)> Keys);

        public Model(string folder)
        {
            var conflicting = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(folder, "*.tsv"))
            {
                var keys = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
                HashSet<string>? types = null, subtypes = null;
                foreach (var line in File.ReadLines(file).Skip(1))
                {
                    var c = line.Split('\t');
                    if (c.Length < 9 || c[0].Length == 0 || c[0] == "*" || char.IsDigit(c[0][0])) continue;
                    var since = Version(c[2]);
                    if (since is null) continue;   // a key of an extension only
                    var key = c[0];
                    Keys.Add(key);
                    keys[key] = (since, Regex.IsMatch(c[3], @"^\d\.\d$") ? c[3] : null);
                    if (c.Length > 10 && c[10].Length > 0)
                    {
                        if (!_links.TryGetValue(key, out var linked)) _links[key] = linked = new HashSet<string>(StringComparer.Ordinal);
                        foreach (Match m in Regex.Matches(c[10], @"[A-Za-z][A-Za-z0-9_]*"))
                            if (m.Value != "fn" && !m.Value.StartsWith("Since", StringComparison.Ordinal)) linked.Add(m.Value);
                    }
                    if (key is "Type" or "Subtype" && c[8].Length > 0)
                    {
                        var values = Regex.Matches(c[8], @"[A-Za-z][A-Za-z0-9_.#-]*").Select(m => m.Value).Where(v => !v.StartsWith("fn:", StringComparison.Ordinal) && v != "fn").ToHashSet(StringComparer.Ordinal);
                        if (key == "Type") types = values;
                        else subtypes = values;
                    }
                    if (c[7].Length > 0 && !conflicting.Contains(key))
                    {
                        if (Defaults.TryGetValue(key, out var other) && other != c[7])
                        {
                            Defaults.Remove(key);
                            conflicting.Add(key);
                        }
                        else Defaults[key] = c[7];
                    }
                }
                _tables.Add(new Table(Path.GetFileNameWithoutExtension(file), types, subtypes, keys));
            }
        }

        private static string? Version(string since)
        {
            if (Regex.IsMatch(since, @"^\d\.\d$")) return since;
            if (!since.Contains("fn:Extension", StringComparison.Ordinal)) return null;
            var m = Regex.Match(since, @"\|\|\s*(\d\.\d)");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// Why the version written may leave the key of this dictionary out, or null: every table the dictionary can be has the key only from
        /// a later version, or deprecates it by the version written. The tables: those the key it was reached by links to (<paramref name="via"/>),
        /// narrowed by /Type and /Subtype (the values a table allows); without either, every table with the key.
        /// </summary>
        public string? VersionReason(PdfDictionary? owner, string? via, string key, string version)
        {
            var type = owner?.GetName(PdfName.Type)?.Value;
            var subtype = owner?.GetName(PdfName.Subtype)?.Value;
            var linked = via is not null && _links.TryGetValue(via, out var names) ? names : null;
            var candidates = _tables.Where(t => t.Keys.ContainsKey(key) && (linked is null || linked.Contains(t.Name)) &&
                                                (type is null || t.Types?.Contains(type) == true) &&
                                                (subtype is null || t.Subtypes is null || t.Subtypes.Contains(subtype))).ToList();
            if (candidates.Count == 0 && linked is not null)
                candidates = _tables.Where(t => t.Keys.ContainsKey(key) && (type is null || t.Types?.Contains(type) == true) &&
                                                (subtype is null || t.Subtypes is null || t.Subtypes.Contains(subtype))).ToList();
            if (candidates.Count == 0) candidates = _tables.Where(t => t.Keys.ContainsKey(key)).ToList();
            if (candidates.Count == 0) return null;
            var entries = candidates.Select(t => t.Keys[key]).ToList();
            var since = entries.Select(e => e.Since).Min(StringComparer.Ordinal)!;
            if (string.CompareOrdinal(version, since) < 0) return $"erst ab PDF {since} (Arlington)";
            if (entries.All(e => e.Deprecated is { } d && string.CompareOrdinal(version, d) >= 0))
                return $"in PDF {entries.Select(e => e.Deprecated!).Max(StringComparer.Ordinal)} veraltet (Arlington)";
            return null;
        }
    }

    private sealed record Difference(string Kind, string Path, string? Reason);

    private sealed class Walk(PdfDocument a, PdfDocument b, Model model, Dictionary<int, int> pages, string version, PdfSaveResult result)
    {
        public readonly List<string> Foreign = [];
        public readonly List<Difference> Standard = [];
        private readonly HashSet<(int, int)> _seen = [];
        public bool Truncated;
        private int _objects;
        public Dictionary<PdfName, HashSet<PdfName>>? Used;   // the resources the current page uses (null: cannot be told, all stay)

        public void Compare(PdfObject? x, PdfObject? y, string path, bool strict, int depth, PdfDictionary? owner = null, string? via = null)
        {
            if (depth > 64 || Truncated) return;
            if (x is PdfReference rx)
            {
                // a page is the page of the same number
                if (pages.TryGetValue(rx.Id.Number, out var page))
                {
                    if (!(y is PdfReference ry0 && ry0.Id.Number == page)) Note(strict, "verweist nicht auf dieselbe Seite", path, null);
                    return;
                }
                if (y is PdfReference ry && !_seen.Add((rx.Id.Number, ry.Id.Number))) return;
                if (++_objects > MaxObjects)
                {
                    Truncated = true;
                    return;
                }
            }
            var u = Resolve(a, x);
            var v = Resolve(b, y);
            if (u is null or PdfNull) return;   // nothing in the source: nothing to keep
            if (v is null or PdfNull)
            {
                Missing(u, path, strict, owner, via);
                return;
            }
            switch (u)
            {
                case PdfStream s:
                    if (v is not PdfStream t)
                    {
                        Note(strict, "Typ anders", path, null);
                        return;
                    }
                    Dict(s.Dictionary, t.Dictionary, path, strict, depth, stream: true);
                    // an XMP packet: /Subtype /XML, /Type /Metadata or the value of /Metadata (some writers leave out the first two)
                    if ((s.Dictionary.GetName(PdfName.Subtype)?.Value == "XML" || s.Dictionary.GetName(PdfName.Type)?.Value == "Metadata" || LastKey(path) == "Metadata") &&
                        Xmp(s, t, path))
                        return;
                    if (!SameData(s, t))
                        Note(strict, "Daten anders", path, Canonical(s, t) ? CanonicalReason : ImageRepaired(s, t) ? ImageReason : null);
                    return;
                case PdfDictionary d:
                    if (v is not PdfDictionary e)
                    {
                        Note(strict, "Typ anders", path, null);
                        return;
                    }
                    if (Tree(path, d) is { } numbers)
                    {
                        CompareTree(d, e, numbers, path, strict, depth);
                        return;
                    }
                    if (IsField(d) && !Regex.IsMatch(path, @"/Annots\[\d+\]$"))
                    {
                        // a field named elsewhere (calculation order, structure): the same field — by its full name
                        if (FullName(d) != (IsField(e) ? FullName(e) : null)) Note(strict, "anderes Feld", path, null);
                        return;
                    }
                    if (d.GetName(PdfName.Subtype)?.Value == "Widget") (d, e) = (EffectiveField(d), EffectiveField(e));
                    var outer = _settled;
                    var outerFont = _rebuiltWidths;
                    if (d.ContainsKey(PdfName.Get("Rect")) && d.ContainsKey(PdfName.Subtype) && Settled(d)) _settled = path;   // an annotation, by any path
                    if (WidthsRebuilt(d)) _rebuiltWidths = path;
                    Dict(d, e, path, strict, depth, stream: false);
                    _settled = outer;
                    _rebuiltWidths = outerFont;
                    return;
                case PdfArray ar:
                    if (v is not PdfArray br)
                    {
                        Note(strict, "Typ anders", path, null);
                        return;
                    }
                    if (ar.Count == br.Count || (long)ar.Count * br.Count > 4_000_000)
                    {
                        if (ar.Count != br.Count) Note(strict, "Länge anders", path, null);
                        for (var i = 0; i < Math.Min(ar.Count, br.Count); i++) Compare(ar[i], br[i], $"{path}[{i}]", strict, depth + 1);
                        return;
                    }
                    // different lengths: the entries matched in order by what they are (the longest common sequence) — an entry the copy adds
                    // (a structure element given to marked content that had none) is no loss, and does not shift every later one
                    var matched = Align(ar, br);
                    if (matched.Count < ar.Count) Note(strict, "Einträge fehlen", path, null);
                    foreach (var (i, j) in matched) Compare(ar[i], br[j], $"{path}[{i}]", strict, depth + 1);
                    return;
                default:
                    // a page's rotation counts modulo 360 (ISO 32000-2, Table 31: a multiple of 90)
                    if (LastKey(path) == "Rotate" && u.AsNumber() is { } r1 && v.AsNumber() is { } r2 && ((r1 - r2) % 360 + 360) % 360 == 0) return;
                    // a form's wrong /Type written as XObject (ISO 32000-2, 8.10.2; FG 1131)
                    if (LastKey(path) == "Type" && u is PdfName && v is PdfName { Value: "XObject" } && !SameValue(u, v))
                    {
                        Note(strict, "Wert anders", path, "/Type einer Form als XObject geschrieben (ISO 32000-2, 8.10.2; FG 1131)");
                        return;
                    }
                    if (!SameValue(u, v)) Note(strict, "Wert anders", path, RenamedTarget(path, u, v));
                    return;
            }
        }

        private void Missing(PdfObject u, string path, bool strict, PdfDictionary? owner, string? via)
        {
            if (IsEmpty(a, u, 0)) return;   // an empty container carries nothing
            // a destination into the void (its page an object that does not exist) leads nowhere: nothing to keep (FG 692, 798)
            if (LastKey(path) is "OpenAction" or "Dest" or "D" && u is PdfArray { Count: > 0 } destination && destination[0] is PdfReference target &&
                a.Resolve(target.Id) is null or PdfNull)
                return;
            var key = LastKey(path);
            // the version written decides for keys of the standard, also in objects of the standard below a foreign key (Apple keeps a copy
            // of an annotation with its appearance: its deprecated /ProcSet goes in 2.0 as everywhere)
            if (model.Keys.Contains(key) && owner is not null && model.VersionReason(owner, via, key, version) is { } reason)
            {
                Note(false, "fehlt", path, reason);
                return;
            }
            // 2.0: /Name of a font goes, also where its kind does not know it (Type 0) or /Subtype is missing — every font but Type 3 (FG 702, 812)
            if (key == "Name" && string.CompareOrdinal(version, "2.0") >= 0 && owner?.GetName(PdfName.Type)?.Value == "Font" &&
                owner.GetName(PdfName.Subtype)?.Value != "Type3")
            {
                Note(false, "fehlt", path, "in PDF 2.0 veraltet bei Schriften außer Typ 3 (FG 702, 812)");
                return;
            }
            // a copy that keeps the claim of PDF/A-4 has no document information dictionary (FG 707)
            if (path == "Info" && b.Catalog?.Get(PdfName.Get("Metadata")) is PdfStream metadata &&
                XmpProperties(b, metadata)?.GetValueOrDefault("http://www.aiim.org/pdfa/ns/id/part") == "4")
            {
                Note(false, "fehlt", path, "PDF/A-4 behält seine Kennung: kein Dokumentinfo-Wörterbuch (FG 707)");
                return;
            }
            // a name dictionary with nothing but entries of /EmbeddedFiles that embed no file (FG 779)
            if (path == "Katalog/Names" && u is PdfDictionary names && names.Count > 0 && names.Entries.All(entry =>
                    entry.Key.Value == "EmbeddedFiles" && Resolve(a, entry.Value) is PdfDictionary tree && Entries(a, tree, false).Values.All(NotEmbedded)))
            {
                Note(false, "fehlt", path, EmbeddedOnly);
                return;
            }
            // a developer extension the version written cannot carry (FG 488): named in the result of the save
            if (Regex.IsMatch(path, @"/Extensions(/[^/]+)?$") && result.DroppedExtensions.Count > 0 &&
                (key == "Extensions" || result.DroppedExtensions.Contains(key)))
            {
                Note(false, "fehlt", path, "Ankündigung über der geschriebenen Fassung entfällt (ISO 32000-2, 7.12.4; FG 488; Ergebnis des Speicherns)");
                return;
            }
            if (!strict)
            {
                if (key == "Rotate" && u.AsNumber() is { } rotation && rotation % 360 == 0) return;   // no rotation
                if (model.Defaults.TryGetValue(key, out var standardValue) && IsValue(u, standardValue)) return;   // the default value
                if (Unused(path, u)) return;
            }
            Note(strict, "fehlt", path, null);
        }

        /// <summary>A resource (or a whole category) of the page its content does not use: the page names only what it uses (FG 788).</summary>
        private bool Unused(string path, PdfObject value)
        {
            var m = Regex.Match(path, @"^Seite \d+/Resources/([^/\[]+)(?:/([^/\[]+))?$");
            if (!m.Success || Used is null) return false;
            var category = PdfName.Get(m.Groups[1].Value);
            if (category.Value is "ProcSet" || category.Value.StartsWith("Default", StringComparison.Ordinal)) return false;
            var names = Used.GetValueOrDefault(category);
            return m.Groups[2].Success ? names is null || !names.Contains(PdfName.Get(m.Groups[2].Value))
                : names is null || value is PdfDictionary entries && entries.Keys.All(name => !names.Contains(name));
        }

        private void Dict(PdfDictionary d, PdfDictionary e, string path, bool strict, int depth, bool stream)
        {
            var map = !stream && Maps.Contains(LastKey(path));   // /N, /D, /R: a map of states, or the appearance stream itself
            var widget = d.GetName(PdfName.Subtype)?.Value == "Widget";
            foreach (var (key, value) in d.Entries)
            {
                var name = key.Value;
                if (stream && Storage.Contains(name)) continue;
                // the page tree is written anew, pages are compared one by one; fields through their widgets and by their full names
                if (name is "Parent" || depth == 0 && name is "Pages" || name is "Fields" && LastKey(path) == "AcroForm" || widget && name is "Kids") continue;
                var foreign = strict || name == "Private" || !map && !model.Keys.Contains(name);
                Compare(value, e.GetRaw(key), path + "/" + name, foreign, depth + 1, map ? null : d, map ? null : LastKey(path));
            }
        }

        /// <summary>Whether the dictionary is the root of a name tree (false) or number tree (true) the path leads to; null if not a tree.</summary>
        private static bool? Tree(string path, PdfDictionary d)
        {
            if (!(d.ContainsKey(PdfName.Get("Names")) || d.ContainsKey(PdfName.Get("Nums")) || d.ContainsKey(PdfName.Kids))) return null;
            var parts = path.Split('/');
            var key = parts[^1];
            if (key is "PageLabels" or "ParentTree") return true;
            if (key is "IDTree") return false;
            return parts.Length >= 3 && parts[^2] == "Names" && parts[^3] == "Katalog" && NameTrees.Contains(key) ? false : null;
        }

        private void CompareTree(PdfDictionary d, PdfDictionary e, bool numbers, string path, bool strict, int depth)
        {
            var source = Entries(a, d, numbers);
            var copy = Entries(b, e, numbers);
            foreach (var (key, value) in source)
                if (copy.TryGetValue(key, out var kept)) Compare(value, kept, $"{path}{{{key}}}", strict, depth + 1);
                else if (numbers && path == "Katalog/PageLabels" && double.TryParse(key, System.Globalization.CultureInfo.InvariantCulture, out var index) && index >= a.PageCount)
                    continue;   // a label of a page the file does not have (ISO 32000-2, 12.4.2: keys are page indices)
                else if (numbers && path == "Katalog/PageLabels" && Resolve(a, value) is not PdfDictionary)
                    continue;   // no page label dictionary (a number in a damaged tree): no label to keep
                else if (path == "Katalog/Names/EmbeddedFiles" && NotEmbedded(value))
                    Note(false, "Eintrag fehlt", $"{path}{{{key}}}", EmbeddedOnly);
                else if (path == "Katalog/Names/EmbeddedFiles" && Renames.TryGetValue(key, out var renamed))
                {
                    // renamed after its file (FG 909): the entry under its new key keeps everything
                    Note(false, "Schlüssel umbenannt", $"{path}{{{key}}}", AfterItsFile);
                    Compare(value, copy[renamed], $"{path}{{{key}}}", strict, depth + 1);
                }
                else if (!IsEmpty(a, value, 0)) Note(strict, "Eintrag fehlt", $"{path}{{{key}}}", null);
        }

        /// <summary>Why an attachment has another key in the copy.</summary>
        private const string AfterItsFile = "Schlüssel des Anhangs nach seinem Dateinamen /UF bzw. /F, Links mitgezogen (ISO 32000-2, 7.7.4; FG 909)";

        private Dictionary<string, string>? _renames;

        /// <summary>
        /// The attachments the copy has under the name of their file (FG 909): old key → new key, where the source's key is missing in the copy,
        /// the file specification's /UF — else /F — differs from it, and the copy has an entry under that name the source had not.
        /// </summary>
        private Dictionary<string, string> Renames => _renames ??= FindRenames();

        private Dictionary<string, string> FindRenames()
        {
            var renames = new Dictionary<string, string>(StringComparer.Ordinal);
            if (EmbeddedFilesOf(a) is not { } sourceTree || EmbeddedFilesOf(b) is not { } copyTree) return renames;
            var source = Entries(a, sourceTree, false);
            var copy = Entries(b, copyTree, false);
            foreach (var (key, value) in source)
                if (!copy.ContainsKey(key) && Resolve(a, value) is PdfDictionary spec &&
                    (Resolve(a, spec.GetRaw(PdfName.Get("UF"))) as PdfString ?? Resolve(a, spec.GetRaw(PdfName.Get("F"))) as PdfString) is { } file &&
                    file.ToText() is { Length: > 0 } name && name != key && copy.ContainsKey(name) && !source.ContainsKey(name))
                    renames[key] = name;
            return renames;

            static PdfDictionary? EmbeddedFilesOf(PdfDocument doc) =>
                Resolve(doc, doc.Catalog?.GetRaw(PdfName.Get("Names"))) is PdfDictionary names ? Resolve(doc, names.GetRaw(PdfName.Get("EmbeddedFiles"))) as PdfDictionary : null;
        }

        /// <summary>A /GoToE target (…/T/N) that names a renamed attachment by its new key (FG 909); null otherwise.</summary>
        private string? RenamedTarget(string path, PdfObject u, PdfObject v) =>
            path.EndsWith("/T/N", StringComparison.Ordinal) && u is PdfString old && v is PdfString now &&
            Renames.TryGetValue(old.ToText(), out var renamed) && renamed == now.ToText() ? AfterItsFile : null;

        /// <summary>The entries of a tree, the first of a key counting (as read); at most 200 000, depth 32, each node once.</summary>
        private static Dictionary<string, PdfObject> Entries(PdfDocument doc, PdfDictionary root, bool numbers)
        {
            var result = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            void Visit(PdfDictionary node, int depth)
            {
                if (depth > 32 || !seen.Add(node) || result.Count > 200_000) return;
                if (Resolve(doc, node.GetRaw(PdfName.Get(numbers ? "Nums" : "Names"))) is PdfArray pairs)
                    for (var i = 0; i + 1 < pairs.Count; i += 2)
                    {
                        var key = Resolve(doc, pairs[i]) switch
                        {
                            PdfString s => s.ToText(),
                            { } k when k.AsNumber() is { } n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            _ => null,
                        };
                        if (key is not null) result.TryAdd(key, pairs[i + 1]);
                    }
                if (Resolve(doc, node.GetRaw(PdfName.Kids)) is PdfArray kids)
                    foreach (var kid in kids)
                        if (Resolve(doc, kid) is PdfDictionary child) Visit(child, depth + 1);
            }
            Visit(root, 0);
            return result;
        }

        /// <summary>
        /// The XMP packet by its properties: every property must stay, except those the writer updates (FG 613); true if both could be read
        /// (otherwise the bytes are compared).
        /// </summary>
        private bool Xmp(PdfStream s, PdfStream t, string path)
        {
            var source = XmpProperties(a, s);
            if (source is null) return false;
            var copy = XmpProperties(b, t);
            if (copy is null)
            {
                Foreign.Add($"{path}: XMP der Kopie nicht lesbar");
                return true;
            }
            foreach (var (key, value) in source)
            {
                var property = key.Split('#')[0].Split('@')[0];
                var declared = key.Contains('@') ? key[(key.IndexOf('@') + 1)..].Split('#')[0] : null;
                var same = copy.TryGetValue(key, out var kept) && kept == value;
                if (same) continue;
                var reason = XmpUpdated.Contains(property) ? "beim Speichern nachgeführt (FG 613)"
                    : XmpReconciled.Contains(property) ? "Info und XMP abgeglichen, das jüngere gilt (FG 613)"
                    : property.StartsWith("http://www.aiim.org/pdfa/ns/id/", StringComparison.Ordinal) && result.PdfAClaimRemovedFor is not null
                        ? "Kennung PDF/A entfernt, die Kopie erfüllt sie nicht mehr (Ergebnis des Speicherns)"
                    : property.StartsWith("http://www.aiim.org/pdfua/ns/id/", StringComparison.Ordinal) && result.AccessibilityClaimRemoved
                        ? "Kennung PDF/UA entfernt (Ergebnis des Speicherns)"
                    : declared is not null && AccessibilityClaims.IsAccessibility(declared) && result.AccessibilityClaimRemoved && kept is null
                        ? "Erklärung zur Barrierefreiheit entfernt, das Speichern brach sie (ISO 14289, Abschnitt 5; Ergebnis des Speicherns)"
                    : null;
                var what = $"XMP {Short(property)}{(declared is null ? "" : $" ({declared})")} {(kept is null ? "fehlt" : "anders")}";
                if (reason is null) Foreign.Add($"{path}: {what}");
                else Standard.Add(new Difference(declared is null ? what : $"XMP {Short(property)} {(kept is null ? "fehlt" : "anders")}", path, reason));
            }
            return true;
        }

        /// <summary>The annotation (its path) whose appearance the save builds anew, while its parts are compared.</summary>
        private string? _settled;

        /// <summary>
        /// Whether the save builds the appearance of this annotation: in PDF 2.0 every annotation has one (12.5.5) — one without gets it, the
        /// widgets of a form that asks readers to draw them (/NeedAppearances, deprecated) get theirs anew (FG 373, 815).
        /// </summary>
        private bool Settled(PdfDictionary annotation) =>
            string.CompareOrdinal(version, "2.0") >= 0 && (!(Resolve(a, annotation.GetRaw(PdfName.Get("AP"))) is PdfDictionary ap &&
                                                             Resolve(a, ap.GetRaw(PdfName.Get("N"))) is PdfStream or PdfDictionary { Count: > 0 }) ||
                annotation.GetName(PdfName.Subtype)?.Value == "Widget" && a.Catalog?.GetDictionary(PdfName.Get("AcroForm"))?.GetBoolean(PdfName.Get("NeedAppearances")) == true);

        /// <summary>The simple font (its path) whose widths the save writes anew, while its parts are compared.</summary>
        private string? _rebuiltWidths;

        /// <summary>
        /// Whether the save writes the widths of this font anew (FG 886): a simple font (Type 1, multiple master, TrueType) whose /Widths is
        /// there but no array of LastChar − FirstChar + 1 numbers — the copy gets the widths of the display for the codes 0 to 255.
        /// </summary>
        private bool WidthsRebuilt(PdfDictionary font)
        {
            if (font.GetName(PdfName.Type)?.Value != "Font" || font.GetName(PdfName.Subtype)?.Value is not ("Type1" or "MMType1" or "TrueType") ||
                Resolve(a, font.GetRaw(PdfName.Get("Widths"))) is not { } widths || widths is PdfNull)
                return false;
            return !(Resolve(a, font.GetRaw(PdfName.Get("FirstChar"))) is PdfInteger first && Resolve(a, font.GetRaw(PdfName.Get("LastChar"))) is PdfInteger last &&
                     widths is PdfArray array && array.Count == last.Value - first.Value + 1 &&
                     Enumerable.Range(0, array.Count).All(i => Resolve(a, array[i]) is PdfInteger or PdfReal));
        }

        private void Note(bool strict, string kind, string path, string? reason)
        {
            if (reason is null && _rebuiltWidths is { } font && path.StartsWith(font, StringComparison.Ordinal) &&
                (path[font.Length..] is "/FirstChar" or "/LastChar" or "/Widths" || path[font.Length..].StartsWith("/Widths[", StringComparison.Ordinal)))
                reason = "einfache Schrift: /Widths passte nicht zu FirstChar und LastChar — die Breiten der Anzeige für 0 bis 255 eingesetzt (FG 886)";
            if (reason is null && _settled is { } annotation && path.StartsWith(annotation, StringComparison.Ordinal) &&
                (path[annotation.Length..] is "/Rect" || path[annotation.Length..].StartsWith("/Rect[", StringComparison.Ordinal) ||
                 path[annotation.Length..].StartsWith("/AP", StringComparison.Ordinal)))
                reason = "Ansicht gebaut oder festgelegt, in PDF 2.0 Pflicht (FG 373, 815)";
            if (strict)
            {
                Foreign.Add($"{path}: {kind}");
                return;
            }
            var full = $"/{LastKey(path)} {kind}";
            // a /FontDescriptor that was no font descriptor (an outline item, say): its keys, which no font descriptor has, went with it — the
            // copy has the descriptor of the substitute (FG 886); keys a font descriptor has are compared as ever
            if (reason is null && kind == "fehlt" && path.Contains("/Font/", StringComparison.Ordinal) && path.Contains("/FontDescriptor/", StringComparison.Ordinal) &&
                !FontDescriptorKeys.Contains(LastKey(path)))
                reason = "einfache Schrift: /FontDescriptor war keine Schriftbeschreibung — die der Ersatzschrift eingesetzt (FG 886)";
            // a decision's path: where it starts, or (beginning with /) a part it contains
            reason ??= Decisions.FirstOrDefault(d => d.Kind == full &&
                (d.Path.StartsWith('/') ? path.Contains(d.Path, StringComparison.Ordinal) : path.StartsWith(d.Path, StringComparison.Ordinal))).Reason;
            Standard.Add(new Difference(full, path, reason));
        }

        /// <summary>Why content differs that the writer wrote in its canonical form (the copy is exactly that form of the source).</summary>
        private const string CanonicalReason = "Inhalt normgerecht geschrieben, so wie die Anzeige ihn liest (ISO 32000-2, 7.3.3, 8.4.3.6, 8.5.3.1, 8.9.7; FG 930, 931, 1131)";

        /// <summary>
        /// Whether the copy's content is the canonical form of the source's (<see cref="ContentConformance.CanonicalSyntax"/>): malformed
        /// numbers and unfinished operands written as the display reads them — anything else that differs stays a difference.
        /// </summary>
        private bool Canonical(PdfStream s, PdfStream t)
        {
            try
            {
                // with the repairs that keep the meaning, as written for the version (FG 1131: „F“, dash phase, /L of inline images in 2.0)
                return ContentConformance.CanonicalSyntax(a.GetStreamData(s), string.CompareOrdinal(version, "2.0") >= 0) is { } canonical &&
                       canonical.AsSpan().SequenceEqual(b.GetStreamData(t));
            }
            catch (Exception e) when (e is Core.Pdf.PdfException or InvalidDataException or InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>Why the data of an image differs that the writer put in the form the standard asks for.</summary>
        private const string ImageReason =
            "Bilddaten in der Form der Norm geschrieben, ohne Verlust (ISO 32000-2, 7.4.8, 7.4.9; S-A A14, A75, A76; FG 992, 996)";

        /// <summary>
        /// Whether the copy's image data is exactly what <see cref="ImageDataConformance.Repair"/> makes of the source's — bytes before the JPEG
        /// start gone, a bare JPEG 2000 codestream wrapped — and nothing else.
        /// </summary>
        private bool ImageRepaired(PdfStream s, PdfStream t)
        {
            try
            {
                if (s.Dictionary.GetName(PdfName.Subtype)?.Value != "Image") return false;
                var raw = a.GetRawStreamData(s);
                var dict = new PdfDictionary();   // changed in place by Repair: a copy
                foreach (var (key, value) in s.Dictionary.Entries) dict.Set(key, value);
                return ImageDataConformance.Repair(dict, raw, ImageDataConformance.BareOpacity(s.Dictionary, raw, a)) is { } repaired &&
                       repaired.AsSpan().SequenceEqual(b.GetRawStreamData(t));
            }
            catch (Exception e) when (e is Core.Pdf.PdfException or InvalidDataException or InvalidOperationException)
            {
                return false;
            }
        }

        private bool SameData(PdfStream s, PdfStream t)
        {
            try
            {
                // an attachment whose key is missing (only the attachments encrypted, not unlocked): written as stored (FG 560) — it cannot be
                // read without its password
                if (a.Security is { AttachmentsLocked: true } && a.StoredStreamData(s).SequenceEqual(b.StoredStreamData(t))) return true;
                if (a.GetRawStreamData(s).AsSpan().SequenceEqual(b.GetRawStreamData(t)) &&
                    Equals(s.Dictionary.GetRaw(PdfName.Filter)?.ToString(), t.Dictionary.GetRaw(PdfName.Filter)?.ToString()))
                    return true;
                return a.GetStreamData(s).AsSpan().SequenceEqual(b.GetStreamData(t));
            }
            catch (Exception e) when (e is Core.Pdf.PdfException or InvalidDataException or InvalidOperationException)
            {
                return false;
            }
        }

        private static PdfObject? Resolve(PdfDocument doc, PdfObject? value) => value is PdfReference r ? doc.Resolve(r.Id) : value;

        /// <summary>Why an entry of /EmbeddedFiles without an embedded file goes.</summary>
        private const string EmbeddedOnly = "in /EmbeddedFiles nur Dateispezifikationen mit eingebetteter Datei (ISO 32000-2, 7.7.4, Tabelle 32; FG 779)";

        /// <summary>A file specification without an embedded file (/EF with /F or /UF as a stream), or no file specification at all.</summary>
        private bool NotEmbedded(PdfObject value) =>
            !(Resolve(a, value) is PdfDictionary spec && Resolve(a, spec.GetRaw(PdfName.Get("EF"))) is PdfDictionary files &&
              (Resolve(a, files.GetRaw(PdfName.Get("F"))) is PdfStream || Resolve(a, files.GetRaw(PdfName.Get("UF"))) is PdfStream));

        /// <summary>The pairs (source index, copy index) of the longest common sequence of the entries by <see cref="Signature"/>.</summary>
        private List<(int, int)> Align(PdfArray x, PdfArray y)
        {
            var sx = x.Select(item => Signature(a, item, true)).ToArray();
            var sy = y.Select(item => Signature(b, item, false)).ToArray();
            var length = new int[sx.Length + 1, sy.Length + 1];
            for (var i = sx.Length - 1; i >= 0; i--)
                for (var j = sy.Length - 1; j >= 0; j--)
                    length[i, j] = sx[i] == sy[j] ? length[i + 1, j + 1] + 1 : Math.Max(length[i + 1, j], length[i, j + 1]);
            var result = new List<(int, int)>();
            for (int i = 0, j = 0; i < sx.Length && j < sy.Length;)
                if (sx[i] == sy[j]) result.Add((i++, j++));
                else if (length[i + 1, j] >= length[i, j + 1]) i++;
                else j++;
            return result;
        }

        /// <summary>
        /// What an entry is, for matching: a value as written; a page by its number; a dictionary by /S (structure element) with its first
        /// marked-content number and its page, or by /Type and /Subtype.
        /// </summary>
        private string Signature(PdfDocument doc, PdfObject item, bool source)
        {
            if (item is PdfReference r && (source ? pages.ContainsKey(r.Id.Number) : pages.ContainsValue(r.Id.Number)))
                return "Seite " + (source ? pages[r.Id.Number] : r.Id.Number);
            var value = Resolve(doc, item);
            switch (value)
            {
                case PdfDictionary d:
                    var page = d.GetRaw(PdfName.Get("Pg")) is PdfReference pg ? source && pages.TryGetValue(pg.Id.Number, out var mapped) ? mapped : pg.Id.Number : 0;
                    var kids = Resolve(doc, d.GetRaw(PdfName.Get("K")));
                    var first = kids switch
                    {
                        PdfArray array => array.Select(k => Resolve(doc, k)).FirstOrDefault(k => k is PdfInteger)?.ToString() ?? $"#{array.Count}",
                        PdfInteger mcid => mcid.ToString(),
                        _ => "",
                    };
                    return d.GetName(PdfName.Get("S")) is { } role ? $"S {role.Value} {first} {page}" : $"D {d.GetName(PdfName.Type)?.Value} {d.GetName(PdfName.Subtype)?.Value}";
                case PdfStream stream:
                    return $"Strom {stream.Dictionary.GetName(PdfName.Subtype)?.Value}";
                case null:
                    return "null";
                default:
                    return value.AsNumber() is { } n ? "N " + Math.Round(n, 4).ToString(System.Globalization.CultureInfo.InvariantCulture) : value.GetType().Name + " " + value;
            }
        }
    }

    private static string LastKey(string path)
    {
        var slash = path.LastIndexOf('/');
        var key = slash < 0 ? path : path[(slash + 1)..];
        var bracket = key.IndexOfAny(['[', '{']);
        return bracket < 0 ? key : key[..bracket];
    }

    private static bool IsField(PdfDictionary d) => d.ContainsKey(PdfName.Get("FT")) || d.ContainsKey(PdfName.Get("T")) && d.ContainsKey(PdfName.Get("Kids"));

    /// <summary>The fully qualified name of a field (ISO 32000-2, 12.7.4.2): the partial names up the /Parent chain.</summary>
    private static string FullName(PdfDictionary field)
    {
        var parts = new List<string>();
        var node = field;
        for (var depth = 0; node is not null && depth < 64; depth++, node = node.GetDictionary(PdfName.Parent))
            if (node.Get(PdfName.Get("T")) is PdfString t) parts.Insert(0, t.ToText());
        return string.Join(".", parts);
    }

    /// <summary>A widget with what it inherits from its fields, and its full name as /T.</summary>
    private static PdfDictionary EffectiveField(PdfDictionary widget)
    {
        var result = new PdfDictionary();
        foreach (var (key, value) in widget.Entries) result.Set(key, value);
        var node = widget.GetDictionary(PdfName.Parent);
        for (var depth = 0; node is not null && depth < 64; depth++, node = node.GetDictionary(PdfName.Parent))
            foreach (var key in FieldInherited)
                if (!result.ContainsKey(PdfName.Get(key)) && node.GetRaw(PdfName.Get(key)) is { } inherited) result.Set(PdfName.Get(key), inherited);
        var name = FullName(widget);
        if (name.Length > 0) result.Set(PdfName.Get("T"), new PdfString(System.Text.Encoding.UTF8.GetBytes(name)));
        else result.Remove(PdfName.Get("T"));
        return result;
    }

    /// <summary>The page with what it inherits from the page tree (ISO 32000-2, 7.7.3.4).</summary>
    private static PdfDictionary Effective(PdfDictionary page)
    {
        var result = new PdfDictionary();
        foreach (var (key, value) in page.Entries) result.Set(key, value);
        var node = page.GetDictionary(PdfName.Parent);
        for (var depth = 0; node is not null && depth < 64; depth++, node = node.GetDictionary(PdfName.Parent))
            foreach (var key in new[] { "Resources", "MediaBox", "CropBox", "Rotate" })
                if (!result.ContainsKey(PdfName.Get(key)) && node.GetRaw(PdfName.Get(key)) is { } inherited) result.Set(PdfName.Get(key), inherited);
        return result;
    }

    private static bool IsEmpty(PdfDocument doc, PdfObject? value, int depth) => (value is PdfReference r ? doc.Resolve(r.Id) : value) switch
    {
        null or PdfNull => true,
        PdfArray array => array.Count == 0,
        PdfDictionary d => d.Count == 0 || depth < 4 && d.Entries.All(entry => IsEmpty(doc, entry.Value, depth + 1)),
        _ => false,
    };

    /// <summary>Whether a value is the default given in the Arlington tables (a number, a boolean or a name).</summary>
    private static bool IsValue(PdfObject value, string standardValue) => value switch
    {
        _ when value.AsNumber() is { } n => double.TryParse(standardValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && Math.Abs(n - d) < 1e-9,
        PdfBoolean b => standardValue.Equals(b.Value ? "true" : "false", StringComparison.OrdinalIgnoreCase),
        PdfName n => n.Value == standardValue,
        _ => false,
    };

    private static bool SameValue(PdfObject u, PdfObject v)
    {
        if (u.AsNumber() is { } p && v.AsNumber() is { } q) return Math.Abs(p - q) <= 1e-5 * Math.Max(1, Math.Abs(p));
        return (u, v) switch
        {
            (PdfName m, PdfName n) => m.Value == n.Value,
            (PdfBoolean m, PdfBoolean n) => m.Value == n.Value,
            (PdfString m, PdfString n) => m.Bytes.AsSpan().SequenceEqual(n.Bytes) || m.ToText() == n.ToText(),
            _ => u.GetType() == v.GetType() && u.ToString() == v.ToString(),
        };
    }

    private static readonly Dictionary<string, string> Prefixes = new(StringComparer.Ordinal)
    {
        ["http://purl.org/dc/elements/1.1/"] = "dc:", ["http://ns.adobe.com/xap/1.0/"] = "xmp:", ["http://ns.adobe.com/pdf/1.3/"] = "pdf:",
        ["http://ns.adobe.com/xap/1.0/mm/"] = "xmpMM:", ["http://www.aiim.org/pdfa/ns/id/"] = "pdfaid:", ["http://www.aiim.org/pdfua/ns/id/"] = "pdfuaid:",
        ["http://pdfa.org/declarations/"] = "pdfd:",
    };

    private static string Short(string property)
    {
        foreach (var (uri, prefix) in Prefixes)
            if (property.StartsWith(uri, StringComparison.Ordinal)) return prefix + property[uri.Length..];
        return property;
    }

    /// <summary>
    /// The properties of an XMP packet: namespace + name → the text of its value (nested values in document order); several of the same
    /// name numbered (#2, …). Null if it cannot be read as XML.
    /// </summary>
    private static Dictionary<string, string>? XmpProperties(PdfDocument doc, PdfStream stream)
    {
        const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64_000_000 };
            var xml = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new MemoryStream(doc.GetStreamData(stream)), settings)) xml.Load(reader);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(string key, string value)
            {
                var name = key;
                for (var n = 2; result.ContainsKey(name); n++) name = $"{key}#{n}";
                result[name] = value;
            }
            foreach (XmlElement description in xml.GetElementsByTagName("Description", Rdf))
            {
                foreach (XmlAttribute attribute in description.Attributes)
                    if (attribute.NamespaceURI is not (Rdf or "http://www.w3.org/2000/xmlns/" or "http://www.w3.org/XML/1998/namespace") && attribute.NamespaceURI.Length > 0)
                        Add(attribute.NamespaceURI + attribute.LocalName, attribute.Value);
                foreach (XmlNode child in description.ChildNodes)
                    if (child is XmlElement property)
                    {
                        // the PDF Declarations: each entry by its conformsTo (the writer removes those of accessibility alone, FG 613/6.1)
                        if (property.NamespaceURI == AccessibilityClaims.Declarations && property.LocalName == "declarations")
                            foreach (var bag in property.ChildNodes.OfType<XmlElement>())
                                foreach (var entry in bag.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "li" && e.NamespaceURI == Rdf))
                                {
                                    var claim = entry.GetAttribute("conformsTo", AccessibilityClaims.Declarations) is { Length: > 0 } attribute ? attribute
                                        : entry.GetElementsByTagName("conformsTo", AccessibilityClaims.Declarations).OfType<XmlElement>().FirstOrDefault()?.InnerText.Trim();
                                    Add($"{property.NamespaceURI}declarations@{claim}", Text(entry));
                                }
                        else Add(property.NamespaceURI + property.LocalName, Text(property));
                    }
            }
            return result;
        }
        catch (Exception e) when (e is XmlException or Core.Pdf.PdfException or InvalidDataException)
        {
            return null;
        }

        static string Text(XmlElement element)
        {
            var parts = new List<string>();
            foreach (XmlAttribute attribute in element.Attributes)
                if (attribute.NamespaceURI != "http://www.w3.org/2000/xmlns/" && !(attribute.NamespaceURI == Rdf && attribute.LocalName == "about"))
                    parts.Add($"@{attribute.LocalName}={attribute.Value}");
            foreach (XmlNode node in element.ChildNodes)
                if (node is XmlElement child) parts.Add($"<{child.LocalName}>{Text(child)}");
                else if (node is XmlText or XmlCDataSection && node.Value!.Trim().Length > 0) parts.Add(node.Value!.Trim());
            return string.Join("|", parts);
        }
    }

    public static int Run(IReadOnlyList<string> files, Dictionary<string, string> passwords, string version, string? arlington)
    {
        var folder = arlington ?? Path.Combine("tools", "pruefer", "arlington", "tsv", "latest");
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"Arlington-Tabellen nicht gefunden: {folder} (--arlington <Ordner>)");
            return 2;
        }
        var model = new Model(folder);
        int ok = 0, failed = 0, skipped = 0, truncated = 0, unexplainedFiles = 0, refused = 0;
        var explained = new Dictionary<string, (int Count, string Reason, string Example)>(StringComparer.Ordinal);
        var unexplained = new Dictionary<string, (int Count, string Example)>(StringComparer.Ordinal);
        var watch = Stopwatch.StartNew();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var password = passwords.GetValueOrDefault(name);
            PdfDocument source;
            try
            {
                source = PdfDocument.Open(file, password);
                if (source.PageCount == 0) throw new Core.Pdf.PdfException("keine Seiten");
            }
            catch (Exception)
            {
                skipped++;
                continue;
            }
            using (source)
            {
                try
                {
                    using var output = new MemoryStream();
                    var result = PdfWriter.Write(source, PageItem.From(source), output, new PdfSaveOptions { Version = version, ImageCodec = SkiaImageCodec.Instance });
                    using var copy = PdfDocument.Open(output.ToArray(), password);
                    var pages = new Dictionary<int, int>();   // pages, and the catalogue (named by signature references): the same object
                    if (source.Trailer.GetRaw(PdfName.Root) is PdfReference sourceRoot && copy.Trailer.GetRaw(PdfName.Root) is PdfReference copyRoot)
                        pages[sourceRoot.Id.Number] = copyRoot.Id.Number;
                    if (source.Catalog?.GetRaw(PdfName.Pages) is PdfReference sourceTree && source.Resolve(sourceTree.Id) is PdfDictionary &&
                        copy.Catalog?.GetRaw(PdfName.Pages) is PdfReference copyTree)
                        pages[sourceTree.Id.Number] = copyTree.Id.Number;   // the root of the page tree stands for all pages (FG 881) — a node, not the array a damaged catalogue names (FG 423)
                    for (var i = 0; i < Math.Min(source.PageCount, copy.PageCount); i++)
                        if (source.Pages[i].Id is { } p && copy.Pages[i].Id is { } q) pages[p.Number] = q.Number;
                    var walk = new Walk(source, copy, model, pages, result.Version, result);
                    walk.Compare(source.Catalog, copy.Catalog, "Katalog", false, 0);
                    walk.Compare(source.Trailer.GetRaw(PdfName.Info), copy.Trailer.GetRaw(PdfName.Info), "Info", false, 1);
                    if (source.PageCount != copy.PageCount) walk.Foreign.Add($"Seitenzahl {source.PageCount} → {copy.PageCount}");
                    for (var i = 0; i < Math.Min(source.PageCount, copy.PageCount); i++)
                    {
                        walk.Used = ResourceUsage.UsedNames(source, source.Pages[i]);
                        walk.Compare(Effective(source.Pages[i].Dictionary), Effective(copy.Pages[i].Dictionary), $"Seite {i + 1}", false, 1);
                    }
                    if (walk.Truncated) truncated++;
                    var open = 0;
                    foreach (var difference in walk.Standard)
                        if (difference.Reason is { } reason)
                        {
                            var key = $"{difference.Kind} — {reason}";
                            explained[key] = explained.TryGetValue(key, out var seen) ? (seen.Count + 1, reason, seen.Example) : (1, reason, $"{name}: {difference.Path}");
                        }
                        else
                        {
                            open++;
                            unexplained[difference.Kind] = unexplained.TryGetValue(difference.Kind, out var seen) ? (seen.Count + 1, seen.Example) : (1, $"{name}: {difference.Path}");
                        }
                    if (open > 0) unexplainedFiles++;
                    if (files.Count == 1)   // one file: every unexplained difference, in the order found
                        foreach (var difference in walk.Standard.Where(d => d.Reason is null).Take(40)) Console.WriteLine($"  ohne Grund: {difference.Path}: {difference.Kind}");
                    else if (open > 0 && walk.Standard.First(d => d.Reason is null) is var first)
                        Console.WriteLine($"{name}: {open} Normschlüssel ohne Grund, zuerst {first.Path}: {first.Kind}");
                    if (walk.Foreign.Count == 0) ok++;
                    else
                    {
                        failed++;
                        Console.WriteLine($"{name}: {string.Join("; ", walk.Foreign.Take(6))}{(walk.Foreign.Count > 6 ? $" … ({walk.Foreign.Count})" : "")}");
                    }
                }
                catch (Core.Pdf.PdfAttachmentsLockedException)
                {
                    refused++;   // an attachment with a key of its own, not unlocked: the save refuses with its message (FG 4, 560)
                }
                catch (Exception e)
                {
                    failed++;
                    Console.WriteLine($"{name}: {e.GetType().Name}: {e.Message}");
                }
            }
        }
        Console.WriteLine();
        Console.WriteLine("Normschlüssel anders, mit Grund:");
        foreach (var (kind, (count, _, example)) in explained.OrderByDescending(k => k.Value.Count))
            Console.WriteLine($"  {count,6}  {kind}  — {example}");
        Console.WriteLine("Normschlüssel anders, ohne Grund:");
        foreach (var (kind, (count, example)) in unexplained.OrderByDescending(k => k.Value.Count))
            Console.WriteLine($"  {count,6}  {kind}  — {example}");
        Console.WriteLine($"Dateien: {files.Count}, fremde Daten erhalten: {ok}, Abweichungen: {failed}, Normschlüssel ohne Grund in {unexplainedFiles}, " +
                          $"nicht lesbar: {skipped}" + (refused > 0 ? $", Speichern abgelehnt (gesperrter Anhang, mit Meldung): {refused}" : "") + (truncated > 0 ? $", nur teilweise verglichen: {truncated}" : "") + $", Zeit {watch.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 && unexplainedFiles == 0 ? 0 : 1;
    }
}
