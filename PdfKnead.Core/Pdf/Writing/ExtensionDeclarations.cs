using System.Text;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// The developer extensions a file declares in its catalog (/Extensions, ISO 32000-2, 7.12), one building block for the writer, PAdES and
/// later MAC and ISO_ extensions (docs/FEHLERGEDAECHTNIS.md, No. 488):
/// <list type="bullet">
/// <item>/Extensions and every developer extensions dictionary stand directly in the catalog (7.12.1) — <see cref="Resolved"/> takes them
/// out of references;</item>
/// <item>an array of developer extensions dictionaries only from PDF 2.0 (Table 48); ISO_ entries are always an array (ISO/TS 32003,
/// 32004), so they exist in 2.0 files only;</item>
/// <item>no base version above the version of the file (7.12.4) — <see cref="DropAbove"/>;</item>
/// <item>a declaration is raised, never lowered (a higher /ADBE level declares more than PDF-Knead can judge) — <see cref="Raise"/>.</item>
/// </list>
/// Entries of other developers without /URL are left as they are.
/// </summary>
internal static class ExtensionDeclarations
{
    internal static readonly PdfName Key = PdfName.Get("Extensions");
    private static readonly PdfName BaseVersion = PdfName.Get("BaseVersion"), ExtensionLevel = PdfName.Get("ExtensionLevel"),
        Url = PdfName.Get("URL"), ExtensionRevision = PdfName.Get("ExtensionRevision"), Type = PdfName.Get("Type");

    /// <summary>
    /// A copy of /Extensions with the dictionary itself and its developer extensions dictionaries resolved (direct objects); values deeper
    /// down stay as written. Null if there is none.
    /// </summary>
    public static PdfDictionary? Resolved(PdfObject? extensions)
    {
        if (extensions is not PdfDictionary source) return null;
        var copy = new PdfDictionary(source.Count);
        foreach (var (key, _) in source.Entries)
        {
            var value = source.Get(key);
            copy.Set(key, value switch
            {
                PdfDictionary entry => Direct(entry),
                PdfArray list => new PdfArray(Enumerable.Range(0, list.Count).Select(i => list.Get(i) is PdfDictionary e ? Direct(e) : list[i])),
                _ => value,
            });
        }
        return copy;

        static PdfDictionary Direct(PdfDictionary entry)
        {
            var copy = new PdfDictionary(entry.Count);
            foreach (var (key, _) in entry.Entries) copy.Set(key, entry.Get(key));
            return copy;
        }
    }

    /// <summary>The level of a developer extensions dictionary, 0 if it has none.</summary>
    public static int LevelOf(PdfObject entry) => entry is PdfDictionary d && d.Get(ExtensionLevel).AsNumber() is { } level ? (int)level : 0;

    /// <summary>The base version of a developer extensions dictionary, or null.</summary>
    public static string? BaseVersionOf(PdfObject entry) => (entry as PdfDictionary)?.GetName(BaseVersion)?.Value;

    /// <summary>The developer extensions dictionaries of a prefix (one, the elements of an array, or none).</summary>
    public static IEnumerable<PdfDictionary> EntriesOf(PdfDictionary extensions, string prefix) => extensions.Get(PdfName.Get(prefix)) switch
    {
        PdfDictionary one => [one],
        PdfArray list => Enumerable.Range(0, list.Count).Select(list.Get).OfType<PdfDictionary>(),
        _ => [],
    };

    /// <summary>Whether a prefix is declared with this base version at this level or higher.</summary>
    public static bool Declares(PdfDictionary extensions, string prefix, string baseVersion, int level) =>
        EntriesOf(extensions, prefix).Any(e => BaseVersionOf(e) == baseVersion && LevelOf(e) >= level);

    /// <summary>
    /// Declares an extension at least at <paramref name="level"/>: a declaration of the same base version at that level or higher stays,
    /// a lower one is replaced; in a 2.0 file a declaration of another base version is kept beside it in an array (ISO_ always in an
    /// array), below 2.0 the prefix holds one dictionary. ISO_ extensions are not declared below 2.0.
    /// </summary>
    /// <param name="extensions">The resolved /Extensions (see <see cref="Resolved"/>), changed in place.</param>
    /// <param name="prefix">The developer's prefix, e.g. "ADBE", "ESIC", "ISO_".</param>
    /// <param name="baseVersion">The PDF version the extension is based on.</param>
    /// <param name="level">The level needed.</param>
    /// <param name="outputVersion">The version of the file written.</param>
    /// <param name="url">The /URL (written in 2.0 files only).</param>
    /// <param name="revision">The /ExtensionRevision, if any.</param>
    /// <returns>True if <paramref name="extensions"/> changed.</returns>
    public static bool Raise(PdfDictionary extensions, string prefix, string baseVersion, int level, string outputVersion, string? url = null, string? revision = null)
    {
        var pdf20 = PdfWriter.CompareVersions(outputVersion, "2.0") >= 0;
        var iso = prefix == "ISO_";
        if (iso && !pdf20) return false;
        // the levels of ISO_ name different specifications (32001, 32002, 32003 …), not steps of one: only the same level declares it,
        // and the others stay beside it (No. 536)
        // an ISO_ entry of this level with other values (base version, URL, revision of the specification — ISO/TS 32003, 4 and ISO/TS 32004, 4,
        // Table 1 fix them) is replaced, not kept (docs/FEHLERGEDAECHTNIS.md, No. 1103)
        if (iso ? EntriesOf(extensions, prefix).Any(e => LevelOf(e) == level && SameIsoValues(e, baseVersion, url, revision))
                : Declares(extensions, prefix, baseVersion, level)) return false;
        var entry = new PdfDictionary();
        // /Type is optional in ISO 32000-2, Table 49, and named by the tables of the ISO/TS extensions: written always (No. 1103)
        entry.Set(PdfName.Type, PdfName.Get("DeveloperExtensions"));
        entry.Set(BaseVersion, PdfName.Get(baseVersion));
        entry.Set(ExtensionLevel, PdfInteger.Of(level));
        if (pdf20 && url is not null) entry.Set(Url, new PdfString(Encoding.ASCII.GetBytes(url)));
        if (revision is not null) entry.Set(ExtensionRevision, new PdfString(Encoding.ASCII.GetBytes(revision)));
        var name = PdfName.Get(prefix);
        // a lower level of the same base version goes; in 2.0 the other entries stay beside the new one
        var others = EntriesOf(extensions, prefix).Where(e => iso ? LevelOf(e) != level : BaseVersionOf(e) != baseVersion).ToList();
        extensions.Set(name, pdf20 && (iso || others.Count > 0) ? new PdfArray([.. others, entry]) : entry);
        return true;
    }

    /// <summary>Whether an ISO_ entry carries these values (those not given are not compared).</summary>
    private static bool SameIsoValues(PdfDictionary entry, string baseVersion, string? url, string? revision) =>
        BaseVersionOf(entry) == baseVersion
        && (url is null || entry.GetString(Url)?.Bytes is { } u && Encoding.ASCII.GetString(u) == url)
        && (revision is null || entry.GetString(ExtensionRevision)?.Bytes is { } r && Encoding.ASCII.GetString(r) == revision);

    /// <summary>
    /// Removes the declarations of a prefix at exactly this level (for an extension no longer used); an empty array removes the prefix.
    /// </summary>
    /// <returns>True if <paramref name="extensions"/> changed.</returns>
    public static bool Remove(PdfDictionary extensions, string prefix, int level)
    {
        var name = PdfName.Get(prefix);
        switch (extensions.Get(name))
        {
            case PdfDictionary one when LevelOf(one) == level:
                extensions.Remove(name);
                return true;
            case PdfArray list:
                var kept = Enumerable.Range(0, list.Count).Select(list.Get).Where(e => LevelOf(e) != level).ToList();
                if (kept.Count == list.Count) return false;
                if (kept.Count == 0) extensions.Remove(name);
                else extensions.Set(name, new PdfArray(kept));
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Removes what the version written cannot declare: entries based on a later version (7.12.4), ISO_ entries below 2.0, and below 2.0
    /// an array reduced to its dictionary of the highest base version and level the file can carry.
    /// </summary>
    /// <param name="extensions">The resolved /Extensions, changed in place.</param>
    /// <param name="outputVersion">The version of the file written.</param>
    /// <returns>The prefixes whose declarations were removed (empty if none).</returns>
    public static IReadOnlyList<string> DropAbove(PdfDictionary extensions, string outputVersion)
    {
        var pdf20 = PdfWriter.CompareVersions(outputVersion, "2.0") >= 0;
        var dropped = new List<string>();
        bool Fits(PdfObject e) => BaseVersionOf(e) is not { } v || PdfWriter.CompareVersions(v, outputVersion) <= 0;
        foreach (var (key, _) in extensions.Entries.ToList())
        {
            if (key == Type) continue;
            var value = extensions.Get(key);
            if (!pdf20 && key.Value == "ISO_")
            {
                extensions.Remove(key);
                dropped.Add(key.Value);
                continue;
            }
            switch (value)
            {
                case PdfDictionary one when !Fits(one):
                    extensions.Remove(key);
                    dropped.Add(key.Value);
                    break;
                case PdfArray list:
                    var items = Enumerable.Range(0, list.Count).Select(list.Get).ToList();
                    var fitting = items.Where(e => e is not PdfDictionary || Fits(e)).ToList();
                    if (pdf20)
                    {
                        if (fitting.Count == items.Count) break;
                        if (fitting.Count == 0) extensions.Remove(key);
                        else extensions.Set(key, new PdfArray(fitting));
                        dropped.Add(key.Value);
                        break;
                    }
                    // below 2.0 one dictionary: the highest base version, then the highest level
                    var best = fitting.OfType<PdfDictionary>()
                        .OrderByDescending(e => BaseVersionOf(e) ?? "", Comparer<string>.Create(PdfWriter.CompareVersions)).ThenByDescending(LevelOf).FirstOrDefault();
                    if (best is null) extensions.Remove(key);
                    else extensions.Set(key, best);
                    if (best is null || items.Count > 1) dropped.Add(key.Value);
                    break;
            }
        }
        return dropped;
    }

    /// <summary>Whether anything is declared (more than the optional /Type).</summary>
    public static bool Any(PdfDictionary extensions) => extensions.Entries.Any(e => e.Key != Type);
}
