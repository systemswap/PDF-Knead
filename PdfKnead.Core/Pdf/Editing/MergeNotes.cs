using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Signatures;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// „Danach nicht mehr da“ in the window „Seiten zusammenführen“ (KONZEPT.md, section 5.2): what a file whose pages go into another
/// document no longer has afterwards — its open password, its restrictions, its signatures, the names of fields that are taken
/// (renamed as the writer does, „Name_2“), and what is not carried over (attachments, document scripts, title and metadata, page
/// labels, XFA, the output intent, bookmarks if they are left out, what an untagged file costs a tagged target). Only information; nothing
/// is decided here.
/// </summary>
public static class MergeNotes
{
    private const int MaxPerArray = 10_000, MaxDepth = 32;

    private static readonly PdfName AcroForm = PdfName.Get("AcroForm"), Fields = PdfName.Get("Fields"), Kids = PdfName.Get("Kids"),
        T = PdfName.Get("T"), Annots = PdfName.Get("Annots"), Names = PdfName.Get("Names"), EmbeddedFiles = PdfName.Get("EmbeddedFiles"),
        JavaScript = PdfName.Get("JavaScript"), OpenAction = PdfName.Get("OpenAction"), AA = PdfName.Get("AA"), Metadata = PdfName.Get("Metadata"),
        PageLabels = PdfName.Get("PageLabels"), Xfa = PdfName.Get("XFA"), Perms = PdfName.Get("Perms"), DocMdp = PdfName.Get("DocMDP"),
        OutputIntents = PdfName.Get("OutputIntents");

    /// <summary>
    /// The notes for one file, in the order shown. <paramref name="takenNames"/> holds the field names already used by the documents
    /// before this one (the target first); the names of this file's fields on the pages taken are added to it.
    /// </summary>
    /// <param name="source">The file.</param>
    /// <param name="openedWithPassword">Whether it was opened with a password.</param>
    /// <param name="pages">Its pages that are in the result.</param>
    /// <param name="keepBookmarks">Whether bookmarks are carried over.</param>
    /// <param name="takenNames">Field names already taken; extended by this file's.</param>
    /// <param name="target">The document whose pages the result keeps (the open one); null: none (a new file of the files loaded).</param>
    /// <returns>The translated notes; empty if nothing is lost.</returns>
    public static List<string> For(PdfDocument source, bool openedWithPassword, IReadOnlyCollection<PdfPage> pages, bool keepBookmarks,
        ISet<string> takenNames, PdfDocument? target = null)
    {
        var notes = new List<string>();
        if (openedWithPassword) notes.Add(Tr.T("Passwort zum Öffnen → entfällt"));
        if (source.Security is { } security)
        {
            var allowed = security.IsOwner ? PdfPermissions.All : security.Permissions;
            if (!allowed.HasFlag(PdfPermissions.Print)) notes.Add(Tr.T("Drucken gesperrt → gilt nicht mehr"));
            else if (!allowed.HasFlag(PdfPermissions.PrintHighQuality)) notes.Add(Tr.T("Drucken nur in niedriger Auflösung → gilt nicht mehr"));
            if (!allowed.HasFlag(PdfPermissions.Modify)) notes.Add(Tr.T("Ändern gesperrt → gilt nicht mehr"));
            if (!allowed.HasFlag(PdfPermissions.Copy)) notes.Add(Tr.T("Kopieren gesperrt → gilt nicht mehr"));
            if (!allowed.HasFlag(PdfPermissions.Annotate)) notes.Add(Tr.T("Kommentieren gesperrt → gilt nicht mehr"));
            if (!allowed.HasFlag(PdfPermissions.FillForms)) notes.Add(Tr.T("Formulare ausfüllen gesperrt → gilt nicht mehr"));
        }
        var signatures = DigitalSignatures.Find(source).Count;
        if (signatures == 1) notes.Add(Tr.T("Digitale Signatur → ungültig"));
        else if (signatures > 1) notes.Add(Tr.T("{0} digitale Signaturen → ungültig", signatures));
        var catalog = source.Catalog;
        if (catalog?.GetDictionary(Perms)?.ContainsKey(DocMdp) == true) notes.Add(Tr.T("Zertifizierung → entfällt"));
        foreach (var (name, renamed) in Renamed(source, pages, takenNames))
            notes.Add(Tr.T("Formularfeld „{0}“ → „{1}“ (der Name kam schon vor)", name, renamed));
        if (catalog is null) return notes;
        // the structure (docs/ABLAUFPLAN.md, 6.5 step 16): an untagged file makes a tagged target inaccessible (design decision: no tagging
        // of its own, the save says why the PDF/UA identification goes). A tagged file's tree comes along, namespaces too (6.5 g); the rare
        // cases left (a file of PDF 2.0 with Artifact elements into one of 1.7) the save names
        if (!StructureRoles.IsTagged(catalog) && target is not null && StructureRoles.IsTagged(target.Catalog))
            notes.Add(Tr.T("Nicht getaggt → das Ergebnis ist nicht mehr barrierefrei"));
        var names = catalog.GetDictionary(Names);
        // (attachment annotations on the pages come along with their page; only the document's own attachments stay behind)
        if (names?.ContainsKey(EmbeddedFiles) == true) notes.Add(Tr.T("Dateianhänge kommen nicht mit"));
        if (names?.ContainsKey(JavaScript) == true || catalog.ContainsKey(OpenAction) || catalog.ContainsKey(AA))
            notes.Add(Tr.T("Skripte und Aktionen beim Öffnen kommen nicht mit"));
        var info = source.Info;
        if (info.Title is not null || info.Author is not null || info.Subject is not null || info.Keywords is not null || catalog.ContainsKey(Metadata))
            notes.Add(Tr.T("Titel und Metadaten kommen nicht mit"));
        // the output intent holds for the whole document: pages from this file take the target's (or none), and DeviceCMYK on them is
        // converted with another profile
        if (catalog.Get(OutputIntents) is PdfArray { Count: > 0 })
            notes.Add(Tr.T("Eigenes Farbprofil (Ausgabeprofil) kommt nicht mit — CMYK-Farben können sich leicht ändern"));
        if (catalog.GetDictionary(AcroForm)?.ContainsKey(Xfa) == true) notes.Add(Tr.T("XFA-Formular kommt nicht mit (die normalen Felder schon)"));
        if (!keepBookmarks && catalog.ContainsKey(PdfName.Outlines)) notes.Add(Tr.T("Lesezeichen kommen nicht mit"));
        return notes;
    }

    /// <summary>The field names of a document on all its pages (for the target the result starts from).</summary>
    /// <param name="doc">The document.</param>
    /// <returns>The names of its top-level fields.</returns>
    public static HashSet<string> FieldNames(PdfDocument doc)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, _) in TopFields(doc, null)) names.Add(name);
        return names;
    }

    /// <summary>
    /// The top-level fields with a widget on the pages taken whose name is already taken, with the name the writer gives them (the
    /// first free „Name_2“, „Name_3“ …); all names of this file are added to <paramref name="takenNames"/>.
    /// </summary>
    private static List<(string Name, string Renamed)> Renamed(PdfDocument source, IReadOnlyCollection<PdfPage> pages, ISet<string> takenNames)
    {
        var widgets = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var page in pages)
            if (page.Dictionary.Get(Annots) is PdfArray annots)
                for (var i = 0; i < annots.Count && i < MaxPerArray; i++)
                    if (annots.Get(i) is PdfDictionary annot) widgets.Add(annot);
        var renamed = new List<(string, string)>();
        foreach (var (name, _) in TopFields(source, widgets))
        {
            if (takenNames.Contains(name))
            {
                var k = 2;
                while (takenNames.Contains($"{name}_{k}")) k++;
                renamed.Add((name, $"{name}_{k}"));
                takenNames.Add($"{name}_{k}");
            }
            else
            {
                takenNames.Add(name);
            }
        }
        return renamed;
    }

    /// <summary>Top-level fields with a name; with <paramref name="widgets"/> only those with a widget among them.</summary>
    private static IEnumerable<(string Name, PdfDictionary Field)> TopFields(PdfDocument doc, HashSet<PdfDictionary>? widgets)
    {
        if (doc.Catalog?.GetDictionary(AcroForm)?.Get(Fields) is not PdfArray fields) yield break;
        for (var i = 0; i < fields.Count && i < MaxPerArray; i++)
            if (fields.Get(i) is PdfDictionary field && field.GetString(T)?.ToText() is { Length: > 0 } name &&
                (widgets is null || HasWidget(field, widgets, 0)))
                yield return (name, field);
    }

    private static bool HasWidget(PdfDictionary field, HashSet<PdfDictionary> widgets, int depth)
    {
        if (widgets.Contains(field)) return true;
        if (depth >= MaxDepth || field.Get(Kids) is not PdfArray kids) return false;
        for (var i = 0; i < kids.Count && i < MaxPerArray; i++)
            if (kids.Get(i) is PdfDictionary kid && HasWidget(kid, widgets, depth + 1)) return true;
        return false;
    }
}
