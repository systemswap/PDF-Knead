using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>The changes of the later revisions of a file compared with a signed revision.</summary>
/// <param name="Changes">The class of the changes.</param>
/// <param name="Details">For <see cref="LaterChanges.Other"/>: what changed (localised, a few lines).</param>
/// <param name="ChangedFields">Full names of the fields whose value, state or look changed (for field locks).</param>
public sealed record RevisionReport(LaterChanges Changes, IReadOnlyList<string> Details, IReadOnlySet<string> ChangedFields)
{
    /// <summary>
    /// Changes below <see cref="LaterChanges.Other"/> that have a reason worth naming when a certification forbids them (the document
    /// information at „no changes“, decision C of 4.11.2026): their class and the localised reason.
    /// </summary>
    public IReadOnlyList<(LaterChanges Level, string Text)> Named { get; init; } = [];
}

/// <summary>What later revisions changed compared with a signed revision.</summary>
public enum LaterChanges
{
    /// <summary>Nothing (the signature covers the file to its end, or the later revisions change nothing).</summary>
    None,
    /// <summary>
    /// Only data for validating signatures in the long term — the security store (/DSS with its /VRI entries) in the shape PAdES gives it, the
    /// declaration of that extension — and invisible document time stamps. No change at any level of certification, also „no changes“ (P = 1):
    /// ISO 32000-2, 12.8.2.2.1 with Table 257, 12.8.5.3.
    /// </summary>
    ValidationData,
    /// <summary>Only fields filled in and further signatures (allowed for every signature).</summary>
    FormsAndSignatures,
    /// <summary>In addition comments added or changed (allowed for an ordinary signature, not for a certified document of level 1 or 2).</summary>
    Comments,
    /// <summary>Anything else: page content, pages, fields themselves, actions … — “changed after signing”.</summary>
    Other,
}

/// <summary>
/// Classifies the objects a document changed after a signed revision (ISO 32000-2:2020, section 12.8.2.2, permitted changes; the
/// incremental saving attacks of https://pdf-insecurity.org): only changes known to be harmless count as allowed — filling fields,
/// adding signature fields and signatures, comments, the document information. Everything else, including changes that cannot be
/// assigned, counts as <see cref="LaterChanges.Other"/>.
/// </summary>
/// <remarks>
/// Objects are compared as written (references by number, direct values in depth), so a change is attributed to the object that
/// holds it. New objects count only through the changed objects that refer to them. A new object under a number the signed
/// revision already counted (below its /Size) counts as a change, because an unchanged object could already refer to it.
/// </remarks>
public static class RevisionChanges
{
    private const int MaxDetails = 6, MaxScan = 50_000, MaxDepth = 32;

    /// <summary>
    /// Whether a certification allows the later changes (ISO 32000-2:2020, 12.8.2.2.1 and Table 257; also the /P of a field's /Lock, Table
    /// 236 with erratum EC3 #131): validation data and document time stamps at every level, filling in and signing from level 2, comments
    /// at level 3. Without a certification all but <see cref="LaterChanges.Other"/>.
    /// </summary>
    public static bool Permitted(CertificationLevel? level, LaterChanges changes) => changes switch
    {
        LaterChanges.None or LaterChanges.ValidationData => true,
        LaterChanges.FormsAndSignatures => level is not CertificationLevel.NoChanges,
        LaterChanges.Comments => level is null or CertificationLevel.FormsSigningAndComments,
        _ => false,
    };

    private static readonly PdfName AcroForm = PdfName.Get("AcroForm"), Fields = PdfName.Get("Fields"), Annots = PdfName.Get("Annots"),
        FieldType = PdfName.Get("FT"), Value = PdfName.Get("V"), Appearance = PdfName.Get("AP"), AppearanceState = PdfName.Get("AS"),
        Title = PdfName.Get("T"), Widget = PdfName.Get("Widget"), Dss = PdfName.Get("DSS"), ByteRange = PdfName.Get("ByteRange"),
        Sig = PdfName.Get("Sig"), Modified = PdfName.Get("M"), Form = PdfName.Get("Form"), Popup = PdfName.Get("Popup"),
        Selected = PdfName.Get("I"), DefaultAppearance = PdfName.Get("DA"), StructTreeRoot = PdfName.Get("StructTreeRoot"),
        StructElem = PdfName.Get("StructElem"), Names = PdfName.Get("Names"), ProcSet = PdfName.Get("ProcSet");

    /// <summary>The keys of a node of the page tree that decide what is shown (ISO 32000-2, 7.7.3.2 and the inheritable ones of 7.7.3.4).</summary>
    private static readonly HashSet<string> PageTreeKeys = ["Type", "Kids", "Count", "Parent", "Resources", "MediaBox", "CropBox", "Rotate"];

    /// <summary>Compares the current state of the file with an earlier revision of it.</summary>
    /// <param name="earlier">The signed revision (the file's bytes up to its end).</param>
    /// <param name="now">The whole file.</param>
    /// <returns>The class of the changes, what changed, and which fields.</returns>
    public static RevisionReport Classify(PdfDocument earlier, PdfDocument now)
    {
        var details = new List<string>();
        var named = new List<(LaterChanges, string)>();
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var level = LaterChanges.None;
        void Raise(LaterChanges to, string? detail = null)
        {
            if (to > level) level = to;
            if (detail is null) return;
            if (to == LaterChanges.Other)
            {
                if (details.Count < MaxDetails && !details.Contains(detail)) details.Add(detail);
            }
            else if (named.Count < MaxDetails && !named.Contains((to, detail))) named.Add((to, detail));
        }
        RevisionReport Report() => new(level, details, fields) { Named = named };

        if (now.ObjectsChangedSince(earlier) is not { } changed)
        {
            Raise(LaterChanges.Other, Tr.T("Die Querverweise der Datei sind beschädigt."));
            return Report();
        }
        var rootRef = earlier.Trailer.GetRaw(PdfName.Root) as PdfReference;
        if (rootRef is null || now.Trailer.GetRaw(PdfName.Root) is not PdfReference nowRoot)
        {
            Raise(LaterChanges.Other, Tr.T("Der Katalog der Datei wurde ausgetauscht."));
            return Report();
        }
        if (nowRoot.Id.Number != rootRef.Id.Number)
        {
            // the catalog written as a new object (some programs do that with every saving): compared entry by entry like one changed in
            // place; anything but a dictionary is a catalog exchanged (docs/FEHLERGEDAECHTNIS.md, No. 480)
            if (earlier.GetObject(rootRef.Id.Number) is not PdfDictionary oldCatalog || now.GetObject(nowRoot.Id.Number) is not PdfDictionary newCatalog)
            {
                Raise(LaterChanges.Other, Tr.T("Der Katalog der Datei wurde ausgetauscht."));
                return Report();
            }
            Catalog(oldCatalog, newCatalog, now, Raise);
        }
        var known = Known.Of(earlier);
        HashSet<int>? reached = null;   // objects of the signed revision reachable other than through /DSS, found only when needed
        var size = earlier.Trailer.GetInteger(PdfName.Size) ?? 0;
        var infoNumber = (earlier.Trailer.GetRaw(PdfName.Info) as PdfReference)?.Id.Number;
        // the document's metadata stream (XMP, ISO 32000-2, 14.3.2): saving programs update it like the document information — shown nowhere
        var metadataNumber = (earlier.GetObject(rootRef.Id.Number).AsDictionary()?.GetRaw(PdfName.Metadata) as PdfReference)?.Id.Number;

        foreach (var number in changed)
        {
            if (!earlier.HasObject(number))
            {
                if (now.HasObject(number) && number < size) Raise(LaterChanges.Other, Tr.T("Ein zuvor freies Objekt wurde neu belegt."));
                continue;   // new objects count through the objects that refer to them
            }
            if (!now.HasObject(number))
            {
                Raise(LaterChanges.Other, Tr.T("Ein Objekt wurde entfernt."));
                continue;
            }
            var before = earlier.GetObject(number);
            var after = now.GetObject(number);
            if (before is PdfStream { Dictionary: var d } && (d.IsType(PdfName.XRef) || d.IsType(PdfName.ObjStm))) continue;   // containers only
            // written anew without any difference — saving programs rewrite objects they did not touch (the page tree, the catalog …):
            // no change at all (docs/FEHLERGEDAECHTNIS.md, No. 479)
            if (Unchanged(before, after, earlier, now)) continue;
            var beforeDict = before.AsDictionary() ?? new PdfDictionary();
            if (number == rootRef.Id.Number) Catalog(beforeDict, after as PdfDictionary, now, Raise);
            // title, dates of the saving program: allowed with filling in, but named — a certification „no changes“ forbids it
            else if (number == infoNumber) Raise(LaterChanges.FormsAndSignatures, InfoChanged);
            else if (number == metadataNumber && after is PdfStream) Raise(LaterChanges.FormsAndSignatures, MetadataChanged);
            else if (known.Pages.TryGetValue(number, out var page)) Page(beforeDict, after as PdfDictionary, page, earlier, now, Raise, known);
            else if (known.AnnotArrays.TryGetValue(number, out var arrayPage)) Annotations(before as PdfArray, after as PdfArray, arrayPage, earlier, now, Raise, known);
            else if (number == known.AcroForm) FormDictionary(beforeDict, after as PdfDictionary, now, Raise);
            else if (number == known.FieldsArray) FieldList(before as PdfArray, after as PdfArray, now, Raise);
            else if (known.Fields.TryGetValue(number, out var field)) Field(beforeDict, after as PdfDictionary, field, Raise, fields, earlier);
            else if (known.Appearances.TryGetValue(number, out var owner) && after is PdfStream { Dictionary: var form } && form.GetName(PdfName.Subtype) == Form)
            {
                // the look of a filled field, or of a comment, replaced under its number; the look of a signed signature, or a stream
                // the page content draws as well, never changes (that would change what was signed)
                if (owner.Kind == LaterChanges.Other) Raise(LaterChanges.Other, Tr.T("Das Erscheinungsbild einer Signatur oder ein Teil des Seiteninhalts wurde ersetzt."));
                else Raise(owner.Kind);
                fields.UnionWith(owner.Fields);
            }
            // the security store extended under its number by another program (validation data): only in the shape PAdES gives it, never
            // a data stream, and only an object nothing else of the signed revision reaches — the same list as /Certs and as the content of
            // a page would add content to the page
            else if (known.Store.TryGetValue(number, out var role))
            {
                reached ??= ReachedBesideStore(earlier);
                if (before is PdfStream || after is PdfStream || reached.Contains(number))
                    Raise(LaterChanges.Other, Describe(before, number));
                else if (!StoreShape(role, after)) Raise(LaterChanges.Other, StoreUnknown);
                else Raise(LaterChanges.ValidationData);
            }
            else if (before.AsDictionary() is { } dict && (dict.IsType(Sig) || dict.ContainsKey(ByteRange)))
                Raise(LaterChanges.Other, Tr.T("Eine Signatur wurde ersetzt."));
            // the logical structure (tagged PDF) kept up by filling in and commenting: what screen readers follow, nothing is drawn from it
            // (an element may leave out /Type, ISO 32000-2, 14.7.2 — then /S without /Type names it; docs/FEHLERGEDAECHTNIS.md, No. 719)
            else if ((beforeDict.IsType(StructTreeRoot) || IsElement(beforeDict)) && after is PdfDictionary)
                Raise(LaterChanges.FormsAndSignatures, StructureChanged);
            // a node of the parent tree extended for a new field or signature (its widget's /StructParent): every pair it had stays as it was,
            // the new ones name structure elements, /Kids and /Limits only grow — anything else in it stays a change (docs/FEHLERGEDAECHTNIS.md,
            // No. 637; the reference checker counts such a change as an extension of the document, not as a change of what was signed)
            else if (known.ParentTreeNodes.Contains(number) && after is PdfDictionary nodeAfter && ParentTreeExtended(beforeDict, nodeAfter, now))
                Raise(LaterChanges.FormsAndSignatures, StructureChanged);
            // a node of the page tree with only entries changed that decide nothing shown (a writer's own mark such as /ITXT)
            else if (beforeDict.IsType(PdfName.Pages) && after is PdfDictionary pagesAfter && !known.Pages.ContainsKey(number)
                     && ChangedKeys(beforeDict, pagesAfter).All(k => !PageTreeKeys.Contains(k.Value)))
                Raise(LaterChanges.FormsAndSignatures, Tr.T("Am Seitenbaum wurde ein Eintrag ohne Wirkung auf die Anzeige geändert."));
            // the form's default resources (/DR) extended by filling in: fonts for the new looks; a name redefined is a change
            else if (known.FormResources.Contains(number) && after is PdfDictionary resourcesAfter && ResourcesExtended(beforeDict, resourcesAfter, earlier, now))
                Raise(LaterChanges.FormsAndSignatures, null);
            else Raise(LaterChanges.Other, Describe(before, number));
        }
        // the trailer naming another object as the document information (a new one is no changed object)
        if ((now.Trailer.GetRaw(PdfName.Info) as PdfReference)?.Id.Number != infoNumber) Raise(LaterChanges.FormsAndSignatures, InfoChanged);
        return Report();
    }

    private static string InfoChanged => Tr.T("Danach wurden die Dokumenteigenschaften (Titel, Autor, Datum …) geändert.");

    /// <summary>The logical structure (tagged PDF) or the tab order adjusted, as filling in and commenting do (docs/FEHLERGEDAECHTNIS.md, No. 480).</summary>
    private static string StructureChanged => Tr.T("Danach wurden die Struktur für die Barrierefreiheit oder die Tab-Reihenfolge angepasst.");

    /// <summary>
    /// The document's XMP metadata changed or added after signing: like the document information, allowed with filling in but named — a
    /// certification „no changes“ forbids it (docs/FEHLERGEDAECHTNIS.md, No. 477).
    /// </summary>
    private static string MetadataChanged => Tr.T("Danach wurden die Metadaten des Dokuments (XMP) geändert.");

    private static string StoreUnknown => Tr.T("Der Sicherheitsspeicher (/DSS) hat eine unbekannte Form.");

    // ------------------------------------------------------------ the security store (ISO 32000-2:2020, 12.8.4.3, Tables 261 and 262)

    /// <summary>What an object of the security store is: the store, its /VRI dictionary, an entry of it, or one of its lists of streams.</summary>
    private enum StoreRole { Store, Vri, VriEntry, List }

    private static readonly PdfName Certs = PdfName.Get("Certs"), Ocsps = PdfName.Get("OCSPs"), Crls = PdfName.Get("CRLs"), Vri = PdfName.Get("VRI"),
        Cert = PdfName.Get("Cert"), Ocsp = PdfName.Get("OCSP"), Crl = PdfName.Get("CRL"), Tu = PdfName.Get("TU"), Ts = PdfName.Get("TS");

    /// <summary>Whether a value has the shape of its role in the store — nothing else is validation data.</summary>
    private static bool StoreShape(StoreRole role, PdfObject? value) => role switch
    {
        StoreRole.Store => StoreDictionary(value),
        StoreRole.Vri => VriDictionary(value),
        StoreRole.VriEntry => VriEntry(value),
        _ => StreamList(value),
    };

    /// <summary>The store: /Type (absent or /DSS), /Certs, /OCSPs, /CRLs (lists of streams), /VRI — nothing else (Table 261).</summary>
    private static bool StoreDictionary(PdfObject? value)
    {
        if (value is not PdfDictionary dss || value is PdfStream) return false;
        foreach (var (key, _) in dss.Entries)
        {
            var ok = key == PdfName.Type ? dss.Get(key) is PdfName { Value: "DSS" }
                : key == Certs || key == Ocsps || key == Crls ? StreamList(dss.Get(key))
                : key == Vri && VriDictionary(dss.Get(key));
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// The /VRI dictionary: each value an entry of the shape of Table 262. A /Type /VRI on the dictionary itself is taken as well — writers put
    /// it there (the PAdES library of the reference validator does, its test files show it); a name carries nothing that could be shown.
    /// </summary>
    private static bool VriDictionary(PdfObject? value)
    {
        if (value is not PdfDictionary vri || value is PdfStream || vri.Count > MaxScan) return false;
        foreach (var (key, _) in vri.Entries)
            if (!(key == PdfName.Type ? vri.Get(key) is PdfName { Value: "VRI" } : VriEntry(vri.Get(key)))) return false;
        return true;
    }

    /// <summary>An entry of /VRI: /Type (absent or /VRI), /Cert, /CRL, /OCSP (lists of streams), /TU (a date), /TS (a stream) — nothing else.</summary>
    private static bool VriEntry(PdfObject? value)
    {
        if (value is not PdfDictionary entry || value is PdfStream) return false;
        foreach (var (key, raw) in entry.Entries)
        {
            var ok = key == PdfName.Type ? entry.Get(key) is PdfName { Value: "VRI" }
                : key == Cert || key == Crl || key == Ocsp ? StreamList(entry.Get(key))
                : key == Tu ? entry.Get(key) is PdfString
                : key == Ts && raw is PdfReference && entry.Get(key) is PdfStream;
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>A list of the store: references to streams only.</summary>
    private static bool StreamList(PdfObject? value)
    {
        if (value is not PdfArray list || list.Count > MaxScan) return false;
        for (var i = 0; i < list.Count; i++)
            if (list[i] is not PdfReference || list.Get(i) is not PdfStream) return false;
        return true;
    }

    /// <summary>
    /// The objects of the signed revision reachable from its trailer other than through the catalog's /DSS: an object of the store among
    /// them is shared with something shown (a shadow attack). Past the bounds everything counts as reached (cautious).
    /// </summary>
    private static HashSet<int> ReachedBesideStore(PdfDocument earlier)
    {
        var reached = new HashSet<int>();
        var pending = new Stack<(PdfObject Value, int Depth)>();
        foreach (var (key, value) in earlier.Trailer.Entries)
            if (key == PdfName.Root || key == PdfName.Info) pending.Push((value, 0));
        var rootNumber = (earlier.Trailer.GetRaw(PdfName.Root) as PdfReference)?.Id.Number;
        while (pending.Count > 0)
        {
            var (value, depth) = pending.Pop();
            if (depth > MaxDepth * 8) continue;
            switch (value)
            {
                case PdfReference reference:
                    if (!reached.Add(reference.Id.Number)) break;
                    if (reached.Count > MaxScan) return Everything(earlier);
                    var target = earlier.HasObject(reference.Id.Number) ? SafeObject(earlier, reference.Id.Number) : null;
                    if (target is PdfDictionary catalog && target is not PdfStream && reference.Id.Number == rootNumber)
                    {
                        foreach (var (key, inner) in catalog.Entries)
                            if (key != Dss) pending.Push((inner, depth + 1));
                    }
                    else if (target is not null) pending.Push((target, depth + 1));
                    break;
                case PdfStream stream:
                    pending.Push((stream.Dictionary, depth + 1));
                    break;
                case PdfDictionary dict:
                    foreach (var (_, inner) in dict.Entries) pending.Push((inner, depth + 1));
                    break;
                case PdfArray array:
                    for (var i = 0; i < Math.Min(array.Count, MaxScan); i++) pending.Push((array[i], depth + 1));
                    break;
            }
        }
        return reached;
    }

    /// <summary>Every object number of a file (the walk ran past its bounds: nothing of the store counts as validation data then).</summary>
    private static HashSet<int> Everything(PdfDocument doc)
    {
        var all = new HashSet<int>();
        var size = (int)Math.Clamp(doc.Trailer.GetInteger(PdfName.Size) ?? 0, 0, 10_000_000);
        for (var n = 0; n < size; n++) all.Add(n);
        return all;
    }

    private static void Catalog(PdfDictionary before, PdfDictionary? after, PdfDocument now, Action<LaterChanges, string?> raise)
    {
        if (after is null)
        {
            raise(LaterChanges.Other, Tr.T("Der Katalog der Datei wurde ausgetauscht."));
            return;
        }
        foreach (var key in ChangedKeys(before, after))
        {
            if (key == AcroForm) FormDictionary(before.GetDictionary(AcroForm) ?? new PdfDictionary(), after.GetDictionary(AcroForm), now, raise);
            // validation data for signatures (PAdES): only in the shape of Table 261 — never take a dictionary whole (No. 334)
            else if (key == Dss)
            {
                if (after.GetRaw(Dss) is null) raise(LaterChanges.Other, Tr.T("Im Katalog der Datei wurde „{0}“ geändert.", key.Value));
                else if (StoreDictionary(after.Get(Dss))) raise(LaterChanges.ValidationData, null);
                else raise(LaterChanges.Other, StoreUnknown);
            }
            // declaring the PAdES extension (or Adobe's) for a later signature: changes nothing that is shown (PadesExtension)
            else if (key.Value == "Extensions" && PadesExtension.OnlyDeclarations(before, after)) raise(LaterChanges.ValidationData, null);
            // the PDF version a later writer declares (the security store needs 1.7 and the extension): nothing shown changes
            else if (key == PdfName.Version && after.GetRaw(key) is PdfName or null) raise(LaterChanges.ValidationData, null);
            else if (key == StructTreeRoot && after.Get(key) is PdfDictionary) raise(LaterChanges.FormsAndSignatures, StructureChanged);
            // the name tree of appearances (/AP) that comment tools fill with the looks of their icons: comments; any other name tree
            // (scripts, embedded files, destinations …) stays a change
            else if (key == Names && OnlyAppearanceNames(before.GetDictionary(Names), after.GetDictionary(Names)))
                raise(LaterChanges.Comments, Tr.T("Für Kommentare wurden Symbole hinterlegt."));
            // the document's metadata stream (XMP) set anew: like the document information (only a stream; anything else is no metadata)
            else if (key == PdfName.Metadata && after.Get(PdfName.Metadata) is PdfStream) raise(LaterChanges.FormsAndSignatures, MetadataChanged);
            // PDF Knead's marker that the file holds its editable annotations: a boolean, it changes nothing that is shown
            else if ((key == Editing.ElementStore.CatalogKey || key == Editing.ElementStore.LegacyCatalogKey) && after.GetRaw(key) is PdfBoolean) raise(LaterChanges.Comments, null);
            else raise(LaterChanges.Other, Tr.T("Im Katalog der Datei wurde „{0}“ geändert.", key.Value));
        }
    }

    private static void FormDictionary(PdfDictionary before, PdfDictionary? after, PdfDocument now, Action<LaterChanges, string?> raise)
    {
        if (after is null)
        {
            raise(LaterChanges.Other, Tr.T("Das Formular wurde entfernt."));
            return;
        }
        // only the list of fields (grown by document time stamps) and the signature flags: validation data; /DR, /DA, /NeedAppearances
        // (readers draw the looks anew) stay filling in
        var keys = ChangedKeys(before, after);
        if (!keys.All(k => k == Fields || k == SigFlags)) raise(LaterChanges.FormsAndSignatures, null);
        foreach (var key in keys)
        {
            if (key == Fields) FieldList(before.Get(Fields) as PdfArray, after.Get(Fields) as PdfArray, now, raise);
            else if (key == SigFlags) raise(LaterChanges.ValidationData, null);
            // what filling in and signing touch; the rest (an XFA form shown instead by some readers, the calculation order …) changes
            // what the form shows or does
            else if (!FormKeysOfFilling.Contains(key)) raise(LaterChanges.Other, Tr.T("Im Formular wurde „{0}“ geändert.", key.Value));
        }
    }

    /// <summary>Keys of the form dictionary that filling in and signing change (ISO 32000-2:2020, Table 224): flags, default look and resources.</summary>
    private static readonly HashSet<PdfName> FormKeysOfFilling = [PdfName.Get("SigFlags"), PdfName.Get("NeedAppearances"), PdfName.Get("DR"), PdfName.Get("DA")];

    private static readonly PdfName SigFlags = PdfName.Get("SigFlags");

    /// <summary>
    /// The list of fields may only grow (new signature fields; only document time stamps: validation data); a field that disappears is a change.
    /// </summary>
    private static void FieldList(PdfArray? before, PdfArray? after, PdfDocument now, Action<LaterChanges, string?> raise)
    {
        var old = References(before);
        var current = References(after);
        if (!old.IsSubsetOf(current)) raise(LaterChanges.Other, Tr.T("Formularfelder wurden entfernt."));
        else if (current.Where(n => !old.Contains(n)).All(n => now.HasObject(n) && SafeObject(now, n) is PdfDictionary added && IsDocumentTimestampWidget(added)))
            raise(LaterChanges.ValidationData, null);
        else raise(LaterChanges.FormsAndSignatures, null);
    }

    private static PdfObject? SafeObject(PdfDocument doc, int number)
    {
        try
        {
            return doc.GetObject(number);
        }
        catch (PdfException)
        {
            return null;
        }
    }

    private static readonly HashSet<string> TimestampWidgetKeys = ["Type", "Subtype", "FT", "T", "TU", "TM", "V", "Rect", "F", "P", "Ff", "AP", "MK", "DA"];

    /// <summary>
    /// Whether a new widget is an invisible document time stamp and nothing more (ISO 32000-2:2020, 12.8.5.2; 12.7.5.5 for „invisible“): a
    /// signature field (/FT /Sig of its own) whose value is a time stamp (/SubFilter /ETSI.RFC3161, /Type absent or /DocTimeStamp, no
    /// /Reference), with no area or hidden (/F Hidden or NoView), without actions, kids, layer or structure, a look only with no area, and
    /// no key beyond those of such a field. A visible „time stamp“ with a look of its own (a shadow attack) is none.
    /// </summary>
    private static bool IsDocumentTimestampWidget(PdfDictionary widget)
    {
        // (a stream is no PdfDictionary — it holds one —, so neither the widget nor its value can be one here: No. 641)
        if (widget.GetName(PdfName.Subtype) != Widget || widget.GetName(FieldType) != Sig) return false;
        foreach (var (key, _) in widget.Entries)
            if (!TimestampWidgetKeys.Contains(key.Value)) return false;
        if (widget.Get(Value) is not PdfDictionary stamp || stamp.GetName(PdfName.Get("SubFilter"))?.Value != "ETSI.RFC3161"
            || stamp.ContainsKey(PdfName.Get("Reference")) || stamp.GetName(PdfName.Type) is { } type && type.Value != "DocTimeStamp")
            return false;
        var noArea = PdfRectangle.From(widget.Get(PdfName.Get("Rect"))) is not { } rect || rect.Width == 0 || rect.Height == 0;
        var hidden = ((widget.GetInteger(PdfName.Get("F")) ?? 0) & (2 | 32)) != 0;
        if (widget.ContainsKey(Appearance) && !noArea) return false;
        return noArea || hidden;
    }

    /// <summary>A page may only change its annotations.</summary>
    private static void Page(PdfDictionary before, PdfDictionary? after, int page, PdfDocument earlier, PdfDocument now, Action<LaterChanges, string?> raise,
        Known known)
    {
        if (after is null)
        {
            raise(LaterChanges.Other, Tr.T("Seite {0} wurde ausgetauscht.", page + 1));
            return;
        }
        foreach (var key in ChangedKeys(before, after))
        {
            if (key == Annots) Annotations(before.Get(Annots) as PdfArray, after.Get(Annots) as PdfArray, page, earlier, now, raise, known);
            // resources only added (for the looks of new fields or comments): every name the content uses keeps its value
            else if (key == PdfName.Resources && Inherited(before, key, earlier) is PdfDictionary oldResources
                     && Resolved(after.GetRaw(key), now) is PdfDictionary newResources && ResourcesExtended(oldResources, newResources, earlier, now))
                raise(LaterChanges.FormsAndSignatures, null);
            else if (key.Value is "StructParents" or "Tabs") raise(LaterChanges.FormsAndSignatures, StructureChanged);
            else raise(LaterChanges.Other, key == PdfName.Contents || key == PdfName.Resources
                ? Tr.T("Der Inhalt von Seite {0} wurde geändert.", page + 1)
                : Tr.T("Seite {0} wurde geändert („{1}“).", page + 1, key.Value));
        }
    }

    /// <summary>Annotations may be added: widgets (fields) as filling and signing, others as comments; removing one is a change.</summary>
    private static void Annotations(PdfArray? before, PdfArray? after, int page, PdfDocument earlier, PdfDocument now, Action<LaterChanges, string?> raise,
        Known known)
    {
        var old = References(before);
        var current = References(after);
        if (!old.IsSubsetOf(current))
        {
            raise(LaterChanges.Other, Tr.T("Auf Seite {0} wurden Anmerkungen oder Felder entfernt.", page + 1));
            return;
        }
        foreach (var number in current)
        {
            if (old.Contains(number)) continue;
            // an object of the signed revision hung onto the page would show what was signed elsewhere or not at all — except a signature
            // field the form already had (prepared before signing, its widget not yet on the page) that is now signed or time-stamped
            // (docs/FEHLERGEDAECHTNIS.md, No. 480)
            if (earlier.HasObject(number) && !(known.Fields.TryGetValue(number, out var prepared) && prepared.IsField
                                               && now.GetObject(number) is PdfDictionary widget && DigitalSignatures.InheritedFieldType(widget) == Sig
                                               && (widget.Get(Value) is PdfDictionary || widget.GetDictionary(PdfName.Parent)?.Get(Value) is PdfDictionary)))
            {
                raise(LaterChanges.Other, Tr.T("Auf Seite {0} wurde ein vorhandenes Objekt eingehängt.", page + 1));
                continue;
            }
            var added = now.GetObject(number) as PdfDictionary;
            var subtype = added?.GetName(PdfName.Subtype);
            // a new widget: a signature field is allowed when it holds a signature (signing); an empty one draws a look of its own
            // like a comment (not allowed in a document certified for filling and signing only, ISO 32000-2:2020, Table 257); a new
            // field to fill in changes the form itself
            if (subtype == Widget && IsDocumentTimestampWidget(added!)) raise(LaterChanges.ValidationData, null);   // invisible, nothing more
            else if (subtype == Widget && added is not null)
            {
                var isSig = DigitalSignatures.InheritedFieldType(added) == Sig;
                var signed = added.Get(Value) is PdfDictionary || added.GetDictionary(PdfName.Parent)?.Get(Value) is PdfDictionary;
                raise(!isSig ? LaterChanges.Other : signed ? LaterChanges.FormsAndSignatures : LaterChanges.Comments,
                    Tr.T("Auf Seite {0} wurde ein Formularfeld hinzugefügt.", page + 1));
            }
            else if (subtype is not null) raise(LaterChanges.Comments, null);
            else raise(LaterChanges.Other, Tr.T("Auf Seite {0} wurde ein unbekanntes Objekt eingefügt.", page + 1));
        }
    }

    /// <summary>A field may change its value and look (filling in); an empty signature field may be signed; a signed one never changes.</summary>
    private static void Field(PdfDictionary before, PdfDictionary? after, FieldInfo info, Action<LaterChanges, string?> raise, HashSet<string> changedFields,
        PdfDocument earlier)
    {
        if (after is null)
        {
            raise(LaterChanges.Other, Tr.T("Das Feld „{0}“ wurde ausgetauscht.", info.Name));
            return;
        }
        if (!info.IsField)
        {
            raise(LaterChanges.Comments, null);   // a comment
            return;
        }
        foreach (var key in ChangedKeys(before, after))
        {
            if (info.Signed && (key == Value || key == Appearance))
                raise(LaterChanges.Other, Tr.T("Die Signatur im Feld „{0}“ wurde ausgetauscht.", info.Name));
            else if (key == Appearance && ReusesEarlierObject(earlier, before, after))
                // filling in draws a new look; pointing the field at something already in the signed file could switch prepared,
                // hidden content visible (a shadow attack)
                raise(LaterChanges.Other, Tr.T("Das Feld „{0}“ zeigt danach etwas, das schon vorher in der Datei lag.", info.Name));
            // the value, its look, the selected entries of a choice field (/I, ISO 32000-2, Table 233) and the text attributes a filling
            // program sets (/DA, the size of an auto-sized field)
            else if (key == Value || key == Appearance || key == AppearanceState || key == Modified || key == Selected || key == DefaultAppearance)
            {
                raise(LaterChanges.FormsAndSignatures, null);   // filled in, or an empty signature field signed
                if (key != Modified && info.Name.Length > 0) changedFields.Add(info.Name);
            }
            else
                raise(LaterChanges.Other, Tr.T("Das Feld „{0}“ wurde geändert („{1}“).", info.Name, key.Value));
        }
    }

    /// <summary>
    /// Whether the new appearance of a field refers to an object of the signed revision that was not already part of the field's own
    /// appearance (normal, rollover and down appearance, each a stream or a dictionary of states).
    /// </summary>
    private static bool ReusesEarlierObject(PdfDocument earlier, PdfDictionary before, PdfDictionary after)
    {
        var own = AppearanceReferences(before.GetDictionary(Appearance));
        return AppearanceReferences(after.GetDictionary(Appearance)).Any(number => earlier.HasObject(number) && !own.Contains(number));
    }

    private static HashSet<int> AppearanceReferences(PdfDictionary? appearance)
    {
        var numbers = new HashSet<int>();
        if (appearance is null) return numbers;
        foreach (var (_, look) in appearance.Entries)
        {
            if (look is PdfReference direct) numbers.Add(direct.Id.Number);
            if (look is PdfDictionary states)
                foreach (var (_, state) in states.Entries)
                    if (state is PdfReference stateRef) numbers.Add(stateRef.Id.Number);
        }
        return numbers;
    }

    private static string Describe(PdfObject before, int number)
    {
        var dict = before.AsDictionary();
        return dict?.GetName(PdfName.Type) is { } type ? Tr.T("Ein Objekt der Art „{0}“ wurde geändert.", type.Value)
            : before is PdfStream ? Tr.T("Ein Datenstrom (Objekt {0}) wurde geändert.", number)
            : Tr.T("Objekt {0} wurde geändert.", number);
    }

    /// <summary>
    /// Whether resources were only extended: every category and every name of before is still there with the same value (references by
    /// number — a changed object behind one is judged on its own); new names and categories are allowed, the list of procedure sets is
    /// meaningless (ISO 32000-2, 14.2). A redefined name could change what existing content draws.
    /// </summary>
    private static bool ResourcesExtended(PdfDictionary before, PdfDictionary after, PdfDocument earlier, PdfDocument now)
    {
        foreach (var (category, value) in before.Entries)
        {
            if (category == ProcSet) continue;
            if (Resolved(value, earlier) is PdfDictionary names)
            {
                if (Resolved(after.GetRaw(category), now) is not PdfDictionary newNames) return false;
                foreach (var (name, entry) in names.Entries)
                    if (newNames.GetRaw(name) is not { } other || !Same(entry, other, 0)) return false;
            }
            else if (after.GetRaw(category) is not { } other || !Same(value, other, 0)) return false;
        }
        return true;
    }

    /// <summary>A page's entry, from the page tree above it if the page has none (inheritable, ISO 32000-2, 7.7.3.4).</summary>
    private static PdfObject? Inherited(PdfDictionary page, PdfName key, PdfDocument doc)
    {
        var node = page;
        for (var depth = 0; node is not null && depth < MaxDepth; depth++)
        {
            if (Resolved(node.GetRaw(key), doc) is { } value) return value;
            node = Resolved(node.GetRaw(PdfName.Parent), doc) as PdfDictionary;
        }
        return null;
    }

    /// <summary>A value with a reference followed in the given revision.</summary>
    private static PdfObject? Resolved(PdfObject? value, PdfDocument doc) =>
        value is PdfReference reference ? doc.HasObject(reference.Id.Number) ? doc.GetObject(reference.Id.Number) : null : value;

    /// <summary>Whether two name dictionaries differ only in the tree of appearances (/AP).</summary>
    private static bool OnlyAppearanceNames(PdfDictionary? before, PdfDictionary? after) =>
        after is not null && ChangedKeys(before ?? new PdfDictionary(), after).All(k => k.Value == "AP");

    /// <summary>Whether an object written anew is the same as before: equal values (<see cref="Same"/>), for a stream also the same raw data.</summary>
    private static bool Unchanged(PdfObject before, PdfObject after, PdfDocument earlier, PdfDocument now) => (before, after) switch
    {
        (PdfStream a, PdfStream b) => Same(a.Dictionary, b.Dictionary, 0) && a.DataLength == b.DataLength && a.DataLength <= MaxComparedStream
                                      && earlier.GetRawStreamData(a).AsSpan().SequenceEqual(now.GetRawStreamData(b)),
        (PdfStream, _) or (_, PdfStream) => false,
        _ => Same(before, after, 0),
    };

    /// <summary>Streams larger than this are not compared byte for byte (they count as changed).</summary>
    private const long MaxComparedStream = 64L << 20;

    /// <summary>
    /// Whether a node of the parent tree (a number tree, ISO 32000-2, 7.9.7 and 14.7.5.4) changed only by growing: no key but /Nums, /Kids and
    /// /Limits changed; every pair of /Nums is still there with the same value, the pairs added name structure elements (or arrays of them, the
    /// MCIDs of a page); /Kids keeps every kid it had in its place and adds others at its end.
    /// </summary>
    private static bool ParentTreeExtended(PdfDictionary before, PdfDictionary after, PdfDocument now)
    {
        if (ChangedKeys(before, after).Any(k => k.Value is not ("Nums" or "Kids" or "Limits"))) return false;
        if (before.GetRaw(Nums) is { } oldNums || after.GetRaw(Nums) is not null)
        {
            if (before.GetRaw(Nums) is not (PdfArray or null) || after.GetRaw(Nums) is not PdfArray newNums) return false;
            var old = before.GetRaw(Nums) as PdfArray ?? [];
            if (newNums.Count < old.Count || newNums.Count % 2 != 0) return false;
            for (var i = 0; i < old.Count; i++)
                if (!Same(old[i], newNums[i], 0)) return false;
            for (var i = old.Count; i + 1 < newNums.Count && i < MaxScan; i += 2)
                if (newNums[i] is not PdfInteger || !NamesElements(Resolved(newNums[i + 1], now), now)) return false;
        }
        if (before.GetRaw(Kids) is { } || after.GetRaw(Kids) is not null)
        {
            if (before.GetRaw(Kids) is not (PdfArray or null) || after.GetRaw(Kids) is not PdfArray newKids) return false;
            var old = before.GetRaw(Kids) as PdfArray ?? [];
            if (newKids.Count < old.Count) return false;
            for (var i = 0; i < old.Count; i++)
                if (!Same(old[i], newKids[i], 0)) return false;
        }
        return true;
    }

    /// <summary>Whether a dictionary is a structure element: /Type /StructElem, or /S without /Type (the type is optional, ISO 32000-2, 14.7.2).</summary>
    private static bool IsElement(PdfDictionary dict) => dict.IsType(StructElem) || dict.ContainsKey(PdfName.Get("S")) && !dict.ContainsKey(PdfName.Type);

    /// <summary>Whether a value of the parent tree names structure elements: one, or an array of them and nulls.</summary>
    private static bool NamesElements(PdfObject? value, PdfDocument now) => value switch
    {
        PdfDictionary element => IsElement(element),
        PdfArray array => array.Count <= MaxScan && Enumerable.Range(0, array.Count).All(i => Resolved(array[i], now) is null or PdfNull ||
                                                                                          NamesElements(Resolved(array[i], now), now) && Resolved(array[i], now) is PdfDictionary),
        _ => false,
    };

    private static readonly PdfName Nums = PdfName.Get("Nums"), Kids = PdfName.Get("Kids");

    /// <summary>Keys whose written value differs.</summary>
    private static List<PdfName> ChangedKeys(PdfDictionary before, PdfDictionary after)
    {
        var keys = new List<PdfName>();
        foreach (var (key, value) in before.Entries)
            if (after.GetRaw(key) is not { } other || !Same(value, other, 0)) keys.Add(key);
        foreach (var (key, _) in after.Entries)
            if (!before.ContainsKey(key)) keys.Add(key);
        return keys;
    }

    /// <summary>Compares two written values: references by number, direct values in depth, numbers by value.</summary>
    private static bool Same(PdfObject a, PdfObject b, int depth)
    {
        if (depth > MaxDepth) return false;
        switch (a)
        {
            case PdfReference ra: return b is PdfReference rb && ra.Id == rb.Id;
            case PdfName na: return b is PdfName nb && na == nb;
            // Numbers by their value: „1.0“ and „1“ are the same number (ISO 32000-2:2020, 7.3.3), and a writer may write either
            case PdfInteger or PdfReal: return a.AsNumber() is { } x && b.AsNumber() is { } y && b is PdfInteger or PdfReal && x.Equals(y);
            case PdfBoolean ba: return b is PdfBoolean bb && ba.Value == bb.Value;
            case PdfNull: return b is PdfNull;
            case PdfString sa: return b is PdfString sb && sa.Bytes.AsSpan().SequenceEqual(sb.Bytes);
            case PdfArray aa:
                if (b is not PdfArray ab || aa.Count != ab.Count) return false;
                for (var i = 0; i < aa.Count; i++)
                    if (!Same(aa[i], ab[i], depth + 1)) return false;
                return true;
            case PdfDictionary da:
                if (b is not PdfDictionary db || da.Count != db.Count) return false;
                foreach (var (key, value) in da.Entries)
                    if (db.GetRaw(key) is not { } other || !Same(value, other, depth + 1)) return false;
                return true;
            default:
                return false;   // a direct stream cannot occur; anything unknown counts as different
        }
    }

    private static HashSet<int> References(PdfArray? array)
    {
        var numbers = new HashSet<int>();
        if (array is null) return numbers;
        for (var i = 0; i < Math.Min(array.Count, MaxScan); i++)
            if (array[i] is PdfReference reference) numbers.Add(reference.Id.Number);
        return numbers;
    }

    /// <param name="IsField">Part of the field tree; otherwise a comment (another annotation).</param>
    /// <param name="Signed">A signature field that already holds a signature, or a widget or part of one (the value may be inherited).</param>
    private sealed record FieldInfo(string Name, bool IsField, bool Signed);

    /// <summary>Who shows an appearance stream: what replacing it counts as, and the fields whose look it is.</summary>
    /// <param name="Kind">Filling in (a field), a comment, or <see cref="LaterChanges.Other"/> (a signed signature, or drawn by the page content).</param>
    /// <param name="Fields">Full names of the fields.</param>
    private sealed record AppearanceOwner(LaterChanges Kind, HashSet<string> Fields);

    /// <summary>The indirect objects of the signed revision whose role matters: pages, annotations, fields, appearances.</summary>
    private sealed class Known
    {
        public Dictionary<int, int> Pages { get; } = [];
        public Dictionary<int, int> AnnotArrays { get; } = [];
        public HashSet<int> Annotations { get; } = [];
        public Dictionary<int, FieldInfo> Fields { get; } = [];
        public Dictionary<int, AppearanceOwner> Appearances { get; } = [];

        /// <summary>The indirect objects of the security store of the signed revision (/DSS, /VRI, its entries, their lists) and their role.</summary>
        public Dictionary<int, StoreRole> Store { get; } = [];

        /// <summary>Annotations and fields with their dictionaries, for the owners of their appearances once all fields are known.</summary>
        private readonly List<(int Number, PdfDictionary Annotation)> _shown = [];
        public int? AcroForm { get; private set; }

        /// <summary>The indirect nodes of the parent tree of the structure (its root and kids, bounded like the other walks).</summary>
        public HashSet<int> ParentTreeNodes { get; } = [];

        /// <summary>The form's default resources (/DR) and their font dictionary, where indirect.</summary>
        public HashSet<int> FormResources { get; } = [];
        public int? FieldsArray { get; private set; }

        public static Known Of(PdfDocument doc)
        {
            var known = new Known();
            var pages = doc.Pages;
            for (var i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                if (page.Id is { } id) known.Pages[id.Number] = i;
                if (page.Dictionary.GetRaw(Annots) is PdfReference annotsRef) known.AnnotArrays[annotsRef.Id.Number] = i;
                if (page.Dictionary.GetArray(Annots) is not { } annots) continue;
                for (var j = 0; j < Math.Min(annots.Count, MaxScan); j++)
                {
                    if (annots[j] is not PdfReference reference || annots.Get(j) is not PdfDictionary annotation) continue;
                    known.Annotations.Add(reference.Id.Number);
                    known._shown.Add((reference.Id.Number, annotation));
                    if (annotation.GetName(PdfName.Subtype) == Widget || annotation.GetName(PdfName.Subtype) == Popup) continue;
                    known.Fields.TryAdd(reference.Id.Number, new FieldInfo("", false, false));
                }
            }
            if (doc.Catalog is { } catalog)
            {
                if (catalog.GetRaw(PdfName.Get("AcroForm")) is PdfReference formRef) known.AcroForm = formRef.Id.Number;
                if (catalog.GetDictionary(PdfName.Get("AcroForm")) is { } form)
                {
                    if (form.GetRaw(PdfName.Get("DR")) is PdfReference drRef) known.FormResources.Add(drRef.Id.Number);
                    if (form.GetDictionary(PdfName.Get("DR"))?.GetRaw(PdfName.Get("Font")) is PdfReference fontRef) known.FormResources.Add(fontRef.Id.Number);
                    if (form.GetRaw(RevisionChanges.Fields) is PdfReference fieldsRef) known.FieldsArray = fieldsRef.Id.Number;
                    if (form.GetArray(RevisionChanges.Fields) is { } fields) known.Walk(fields, "", false, false, 0);
                }
            }
            known.PageWidgets(doc);
            known.Owners(doc);
            known.StoreObjects(doc);
            known.ParentTreeObjects(doc);
            return known;
        }

        /// <summary>The indirect nodes of the structure's parent tree: from /ParentTree of the root through /Kids (depth 32, bounded).</summary>
        private void ParentTreeObjects(PdfDocument doc)
        {
            if (doc.Catalog?.GetDictionary(StructTreeRoot) is not { } root) return;
            var stack = new Stack<(PdfObject? Raw, int Depth)>();
            stack.Push((root.GetRaw(PdfName.Get("ParentTree")), 0));
            while (stack.TryPop(out var item) && ParentTreeNodes.Count < MaxScan)
            {
                if (item.Depth > 32 || item.Raw is not PdfReference reference || !ParentTreeNodes.Add(reference.Id.Number)) continue;
                if (doc.Resolve(reference.Id) is not PdfDictionary node || node.GetArray(Kids) is not { } kids) continue;
                for (var i = 0; i < Math.Min(kids.Count, MaxScan); i++) stack.Push((kids[i], item.Depth + 1));
            }
        }

        /// <summary>The indirect objects of the catalog's /DSS: the store, /VRI and its entries, the lists of streams (bounded like the other walks).</summary>
        private void StoreObjects(PdfDocument doc)
        {
            if (doc.Catalog is not { } catalog || catalog.GetRaw(Dss) is not { } dssRaw) return;
            void Note(PdfObject? raw, StoreRole role)
            {
                if (raw is PdfReference reference && Store.Count < MaxScan) Store.TryAdd(reference.Id.Number, role);
            }
            Note(dssRaw, StoreRole.Store);
            // (a stream is no PdfDictionary: a store, /VRI or entry that is a stream stops here — No. 641)
            if (catalog.Get(Dss) is not PdfDictionary dss) return;
            foreach (var list in (PdfName[])[Certs, Ocsps, Crls]) Note(dss.GetRaw(list), StoreRole.List);
            Note(dss.GetRaw(Vri), StoreRole.Vri);
            if (dss.Get(Vri) is not PdfDictionary vri) return;
            foreach (var (key, raw) in vri.Entries)
            {
                if (Store.Count >= MaxScan) return;
                Note(raw, StoreRole.VriEntry);
                if (vri.Get(key) is not PdfDictionary entry) continue;
                foreach (var list in (PdfName[])[Cert, Ocsp, Crl]) Note(entry.GetRaw(list), StoreRole.List);
            }
        }

        /// <summary>
        /// The owner of each appearance stream: a field to fill in, a comment, or — strictest — a signed signature or a stream the page
        /// content draws too (a prepared file could show the same stream as a field's look and as part of the page).
        /// </summary>
        private void Owners(PdfDocument doc)
        {
            var content = ContentObjects(doc);
            foreach (var (number, annotation) in _shown)
            {
                var info = Fields.TryGetValue(number, out var known) ? known : new FieldInfo("", false, false);
                var kind = !info.IsField ? LaterChanges.Comments : info.Signed ? LaterChanges.Other : LaterChanges.FormsAndSignatures;
                foreach (var look in AppearanceNumbers(annotation))
                {
                    var owner = Appearances.TryGetValue(look, out var existing) ? existing : Appearances[look] = new AppearanceOwner(kind, []);
                    var strictest = content.Contains(look) ? LaterChanges.Other : (LaterChanges)Math.Max((int)owner.Kind, (int)kind);
                    if (strictest != owner.Kind) Appearances[look] = owner = owner with { Kind = strictest };
                    if (info.IsField && info.Name.Length > 0) owner.Fields.Add(info.Name);
                }
            }
        }

        /// <summary>The form XObjects the pages draw (their resources, and forms inside forms; bounded against prepared files).</summary>
        private static HashSet<int> ContentObjects(PdfDocument doc)
        {
            var found = new HashSet<int>();
            var pending = new Stack<(PdfDictionary? Resources, int Depth)>();
            foreach (var page in doc.Pages) pending.Push((page.Resources, 0));
            while (pending.Count > 0 && found.Count < MaxScan)
            {
                var (resources, depth) = pending.Pop();
                if (resources?.GetDictionary(PdfName.Get("XObject")) is not { } xobjects) continue;
                foreach (var (_, value) in xobjects.Entries)
                    if (value is PdfReference reference && found.Add(reference.Id.Number) && depth < MaxDepth
                        && doc.Resolve(reference.Id) is PdfStream { Dictionary: var form } && form.GetName(PdfName.Subtype) == Form)
                        pending.Push((form.GetDictionary(PdfName.Resources), depth + 1));
            }
            return found;
        }

        private int _visited;

        private void Walk(PdfArray list, string prefix, bool inheritedSig, bool inheritedSigned, int depth)
        {
            if (depth > MaxDepth) return;
            for (var i = 0; i < Math.Min(list.Count, MaxScan) && _visited < MaxScan; i++)
            {
                if (list[i] is not PdfReference reference || list.Get(i) is not PdfDictionary field
                    || Fields.TryGetValue(reference.Id.Number, out var seen) && seen.IsField)
                    continue;
                _visited++;
                var part = field.GetString(Title)?.ToText();
                var name = part is null ? prefix : prefix.Length == 0 ? part : $"{prefix}.{part}";
                var isSig = field.GetName(FieldType) is { } type ? type == Sig : inheritedSig;
                // a widget below a signed signature field is part of the signature (its look must not change)
                var signed = isSig && (inheritedSigned || field.Get(Value) is PdfDictionary);
                Fields[reference.Id.Number] = new FieldInfo(name, true, signed);
                _shown.Add((reference.Id.Number, field));
                if (field.GetArray(PdfName.Kids) is { } kids) Walk(kids, name, isSig, signed, depth + 1);
            }
        }

        /// <summary>
        /// Widgets on the pages that the field list does not reach (files exist whose fields hang only on the pages): they and their
        /// parents up the /Parent chain are fields as well — filling them in is filling in, not some other change.
        /// </summary>
        private void PageWidgets(PdfDocument doc)
        {
            foreach (var page in doc.Pages)
            {
                if (page.Dictionary.GetArray(Annots) is not { } annots) continue;
                for (var j = 0; j < Math.Min(annots.Count, MaxScan) && _visited < MaxScan; j++)
                {
                    if (annots[j] is not PdfReference reference || annots.Get(j) is not PdfDictionary widget || widget.GetName(PdfName.Subtype) != Widget) continue;
                    var (number, node) = (reference.Id.Number, widget);
                    for (var depth = 0; depth <= MaxDepth; depth++)
                    {
                        if (!Fields.TryGetValue(number, out var seen) || !seen.IsField)
                        {
                            _visited++;
                            var name = FullName(doc, node);
                            var isSig = DigitalSignatures.InheritedFieldType(node) == Sig;
                            Fields[number] = new FieldInfo(name, true, isSig && SignedUp(doc, node));
                        }
                        if (node.GetRaw(PdfName.Parent) is not PdfReference parent || doc.Resolve(parent.Id) is not PdfDictionary up) break;
                        (number, node) = (parent.Id.Number, up);
                    }
                }
            }
        }

        /// <summary>Full name of a field: the partial names up the /Parent chain, joined with dots.</summary>
        private static string FullName(PdfDocument doc, PdfDictionary node)
        {
            var parts = new List<string>();
            for (var (current, depth) = (node, 0); current is not null && depth <= MaxDepth; depth++)
            {
                if (current.GetString(Title)?.ToText() is { } part) parts.Insert(0, part);
                current = current.GetRaw(PdfName.Parent) is PdfReference parent ? doc.Resolve(parent.Id) as PdfDictionary : null;
            }
            return string.Join('.', parts);
        }

        /// <summary>Whether a node or one of its parents holds a signature (/V, inherited by the widgets below).</summary>
        private static bool SignedUp(PdfDocument doc, PdfDictionary node)
        {
            for (var (current, depth) = (node, 0); current is not null && depth <= MaxDepth; depth++)
            {
                if (current.Get(Value) is PdfDictionary) return true;
                current = current.GetRaw(PdfName.Parent) is PdfReference parent ? doc.Resolve(parent.Id) as PdfDictionary : null;
            }
            return false;
        }

        /// <summary>The appearance streams of an annotation (/AP with /N, /R, /D, each a stream or a dictionary of states).</summary>
        private static List<int> AppearanceNumbers(PdfDictionary annotation)
        {
            var numbers = new List<int>();
            if (annotation.GetDictionary(Appearance) is not { } ap) return numbers;
            foreach (var (_, look) in ap.Entries)
            {
                if (look is PdfReference direct) numbers.Add(direct.Id.Number);
                if (look is PdfDictionary states)
                    foreach (var (_, state) in states.Entries)
                        if (state is PdfReference stateRef) numbers.Add(stateRef.Id.Number);
            }
            return numbers;
        }
    }
}
