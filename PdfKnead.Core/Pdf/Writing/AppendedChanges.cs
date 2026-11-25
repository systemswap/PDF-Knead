using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Signatures;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Saves allowed changes of a digitally signed file as an incremental update, so that its signatures stay valid (KONZEPT.md,
/// section 5.17, step S5): filled-in fields of the file, and new own text boxes, shapes, images and empty signature fields as
/// annotations. Everything else (pages, edited text, redaction, fixed signatures, moving or deleting annotations of the file …)
/// rewrites the file as usual.
/// </summary>
/// <remarks>
/// The ordinary writer writes the changed document into memory first — with its appearances, font subsets and field values —
/// and only the changed parts are taken over from there: field values and their appearances by field name, new own annotations by
/// their description and position. The result is checked like a reader would (<see cref="RevisionChanges"/>): if the update were to
/// change more than filling in fields, signing and commenting, nothing is written.
/// </remarks>
public static class AppendedChanges
{
    private static readonly PdfName AcroForm = PdfName.Get("AcroForm"), Fields = PdfName.Get("Fields"), Value = PdfName.Get("V"),
        AppearanceState = PdfName.Get("AS"), Appearance = PdfName.Get("AP"), FieldType = PdfName.Get("FT"), Widget = PdfName.Get("Widget"),
        Annots = PdfName.Get("Annots"), Rect = PdfName.Get("Rect"), PageRef = PdfName.Get("P"), NeedAppearances = PdfName.Get("NeedAppearances");

    /// <summary>
    /// Whether the difference between the saved pages and the current ones can be appended: the same pages in the same order, the
    /// saved elements still there (fields of the file at most filled in), and new elements only of the kinds saved as annotations —
    /// as far as the signatures of the file allow it (certification level, locked fields). Signatures still to be signed digitally
    /// do not count here (they are appended by <see cref="SignedSaving"/>).
    /// </summary>
    /// <param name="main">The opened document.</param>
    /// <param name="saved">The pages as last saved (or opened).</param>
    /// <param name="pages">The pages to save.</param>
    /// <returns>true if appending is enough (also when nothing changed).</returns>
    /// <remarks>Options that change the whole file (compression, protection, flattening, as images) are the caller's to rule out.</remarks>
    public static bool CanAppend(PdfDocument main, IReadOnlyList<PageItem> saved, IReadOnlyList<PageItem> pages) =>
        CanAppend(main, saved, pages, SignaturePermissions.Of(main), out _);

    /// <inheritdoc cref="CanAppend(PdfDocument, IReadOnlyList{PageItem}, IReadOnlyList{PageItem})"/>
    /// <param name="permissions">The restrictions of the file's signatures (<see cref="SignaturePermissions.Of"/>).</param>
    /// <param name="reason">If a restriction of the signatures is what prevents appending: why (localised); otherwise null.</param>
    public static bool CanAppend(PdfDocument main, IReadOnlyList<PageItem> saved, IReadOnlyList<PageItem> pages, SignaturePermissions permissions,
        out string? reason)
    {
        reason = null;
        if (!IncrementalUpdate.CanAppend(main) || pages.Count != saved.Count || pages.Count != main.PageCount) return false;
        for (var i = 0; i < pages.Count; i++)
        {
            var (page, before) = (pages[i], saved[i]);
            if (!ReferenceEquals(page.Source, main) || !ReferenceEquals(before.Source, main) || !ReferenceEquals(page.Page, before.Page)) return false;
            if (page.Page.Index != i || page.Replaces is not null) return false;
            if (!ElementsAppendable(before.Elements, page.Elements.Where(e => e.Digital is null).ToList(), permissions, ref reason)) return false;
        }
        return true;
    }

    /// <summary>The saved elements in their order, each the same or a field of the file filled in; then only new appendable ones.</summary>
    private static bool ElementsAppendable(IReadOnlyList<PageElement> before, IReadOnlyList<PageElement> after, SignaturePermissions permissions, ref string? reason)
    {
        var at = 0;
        foreach (var element in after)
        {
            if (at < before.Count && ReferenceEquals(element, before[at]))
            {
                at++;
                continue;
            }
            if (at < before.Count && IsFilledIn(before[at], element))
            {
                at++;
                var name = FormField.Of((ForeignAnnotation)element)!.Name;
                if (!permissions.AllowsFormsAndSigning) return Refuse(out reason, Tr.T("Das Dokument ist zertifiziert: danach sind keine Änderungen erlaubt."));
                if (permissions.IsLocked(name)) return Refuse(out reason, Tr.T("Das Feld „{0}“ ist durch eine Signatur gesperrt.", name));
                continue;
            }
            if (!IsNewAnnotation(element)) return false;
            if (!permissions.AllowsFormsAndSigning) return Refuse(out reason, Tr.T("Das Dokument ist zertifiziert: danach sind keine Änderungen erlaubt."));
            // an empty signature field counts like a comment: it is no signature yet, but a look of its own (RevisionChanges)
            if (!permissions.AllowsComments)
                return Refuse(out reason, element is SignatureFieldElement
                    ? Tr.T("Das Dokument ist zertifiziert: danach sind nur Ausfüllen und Unterschreiben erlaubt, keine leeren Signaturfelder.")
                    : Tr.T("Das Dokument ist zertifiziert: danach sind nur Ausfüllen und Unterschreiben erlaubt, keine Kommentare."));
        }
        return at == before.Count;
    }

    private static bool Refuse(out string? reason, string text)
    {
        reason = text;
        return false;
    }

    /// <summary>The same field of the file, only with another value (not moved, resized or redesigned).</summary>
    private static bool IsFilledIn(PageElement before, PageElement after) =>
        before is ForeignAnnotation old && after is ForeignAnnotation now && ReferenceEquals(old.Original, now.Original) && now.Value is not null
        && !now.IsMoved && !now.IsResized && now.Spec is null && old.Spec is null && FormField.Of(now) is not null;

    /// <summary>Kinds of new elements that are saved as annotations and count as comments or signature fields.</summary>
    private static bool IsNewAnnotation(PageElement element) => element.Seal is null && element is TextElement { IsField: false } or ShapeElement or DimensionElement
        or CodeElement or ImageElement { IsSignature: false } or SignatureFieldElement;

    /// <summary>Appends the changes to the original file.</summary>
    /// <param name="main">The opened document (it must qualify, see <see cref="CanAppend"/>).</param>
    /// <param name="pages">The pages to save, without signatures still to be signed.</param>
    /// <param name="options">The save options (as for writing anew; they only shape the appearances).</param>
    /// <param name="cancel">Cancels the work; nothing is written.</param>
    /// <returns>The whole file: the original bytes, then the update (or just the original bytes if nothing changed).</returns>
    /// <exception cref="NotAppendableException">The changes cannot be appended without changing more than allowed; nothing is written.</exception>
    public static byte[] Apply(PdfDocument main, IReadOnlyList<PageItem> pages, PdfSaveOptions options, CancellationToken cancel = default)
    {
        using var file = SaveBuffer.Create(main.FileSize);
        ApplyTo(main, pages, options, file, cancel);
        return file.ToArray();
    }

    /// <summary>
    /// Like <see cref="Apply"/>, into a buffer that may receive further updates afterwards (signatures, see <see cref="SignedSaving"/>):
    /// the original bytes, then the update.
    /// </summary>
    /// <param name="main">The opened document (it must qualify, see <see cref="CanAppend"/>).</param>
    /// <param name="pages">The pages to save, without signatures still to be signed.</param>
    /// <param name="options">The save options (as for writing anew; they only shape the appearances).</param>
    /// <param name="file">An empty buffer (<see cref="SaveBuffer.Create"/>); left empty if the changes cannot be appended.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <exception cref="NotAppendableException">The changes cannot be appended without changing more than allowed.</exception>
    internal static void ApplyTo(PdfDocument main, IReadOnlyList<PageItem> pages, PdfSaveOptions options, MemoryStream file, CancellationToken cancel = default)
    {
        // The pages written anew, only to find what changed: compared with the original, the changed parts are appended
        using var buffer = SaveBuffer.Create(main.FileSize);
        // the content of the file as stored: an appearance only the repairs of saving changed would count as changed and be appended — the
        // update carries what is new, new content of its own is written as the version asks (docs/FEHLERGEDAECHTNIS.md, No. 1131)
        PdfWriter.Write(main, pages, buffer, options with { ObjectStreams = false, ContentAsStored = true }, cancel);
        using var rewritten = PdfDocument.Open(SaveBuffer.Source(buffer));
        if (rewritten.PageCount != main.PageCount) throw NotAppendable();
        var update = new IncrementalUpdate(main);
        var copied = new Dictionary<PdfObjectId, PdfReference>();
        for (var i = 0; i < main.PageCount; i++)
            if (rewritten.Pages[i].Id is { } from && main.Pages[i].Id is { } to) copied[from] = new PdfReference(to);

        FieldValues(main, rewritten, update, copied);
        var (added, newFields) = NewAnnotations(main, rewritten, update, copied);
        var catalogChanged = false;
        var catalog = IncrementalParts.Copy(main.Catalog!);
        // the rewritten file settled the appearances of a form with /NeedAppearances (code fields, Fehlergedächtnis 219) and dropped the
        // entry: so does the update, otherwise other readers draw the code fields anew as text (No. 288)
        var dropNeedAppearances = main.Catalog!.GetDictionary(AcroForm)?.GetBoolean(NeedAppearances) == true &&
                                  rewritten.Catalog?.GetDictionary(AcroForm)?.GetBoolean(NeedAppearances) != true;
        catalogChanged |= UpdateForm(main, update, catalog, newFields, dropNeedAppearances);
        if (added > 0 && !ElementStore.IsMarked(main.Catalog))
        {
            catalog.Set(ElementStore.CatalogKey, PdfBoolean.True);   // own elements are read only from marked files
            catalogChanged = true;
        }
        if (catalogChanged) update.Replace(((PdfReference)main.Trailer.GetRaw(PdfName.Root)!).Id, catalog);
        cancel.ThrowIfCancellationRequested();
        for (long at = 0; at < main.FileSize; at += 1 << 20)
            file.Write(main.FileBytes(at, 1 << 20));
        if (update.Count == 0) return;   // nothing changed: just the original bytes
        update.WriteUpdateTo(file);
        // As a reader checks it: the update must not change more than filling in fields, signing and commenting
        using (var check = PdfDocument.Open(SaveBuffer.Source(file)))
            if (RevisionChanges.Classify(main, check) is { Changes: LaterChanges.Other } classified)
            {
                file.SetLength(0);
                throw NotAppendable(classified.Details);
            }
    }

    private static NotAppendableException NotAppendable(IReadOnlyList<string>? reasons = null) =>
        new(Tr.T("Die Änderungen lassen sich nicht an die signierte Datei anhängen. Bitte als neue Datei speichern.")
            + (reasons is { Count: > 0 } ? " " + Tr.T("Grund: {0}", string.Join(" ", reasons)) : ""));

    // ------------------------------------------------------------ Filled-in fields

    /// <summary>
    /// Widgets whose value, state or appearance differs after writing: state and appearance on the widget, the value on its field
    /// (the widget itself or the nearest named node up its /Parent chain) — also the other buttons of a radio group, fields the form
    /// calculates, and fields that only got an appearance. Widgets are paired per page by their position (filling in moves nothing),
    /// so fields that are missing from the form's field list or share a name are found too.
    /// </summary>
    private static void FieldValues(PdfDocument main, PdfDocument rewritten, IncrementalUpdate update, Dictionary<PdfObjectId, PdfReference> copied)
    {
        // One working copy per object: a field can be both a widget on one page and the parent of widgets on others
        var edited = new Dictionary<PdfObjectId, PdfDictionary>();
        PdfDictionary Edit(PdfObjectId id, PdfDictionary original)
        {
            if (!edited.TryGetValue(id, out var copy)) edited[id] = copy = IncrementalParts.Copy(original);
            return copy;
        }
        for (var i = 0; i < main.PageCount; i++)
        {
            var before = Widgets(main.Pages[i].Dictionary.GetArray(Annots));
            var after = Widgets(rewritten.Pages[i].Dictionary.GetArray(Annots));
            var used = new bool[after.Count];
            foreach (var (oldId, oldWidget) in before)
            {
                var k = Enumerable.Range(0, after.Count).FirstOrDefault(n => !used[n] && Similar(oldWidget.Get(Rect), after[n].Dict.Get(Rect), 0), -1);
                if (k < 0) continue;
                used[k] = true;
                var newWidget = after[k].Dict;
                var (fieldId, field) = ValueNode(main, oldId, oldWidget);
                var (_, newField) = ValueNode(rewritten, after[k].Id, newWidget);
                var valueChanged = !SameValue(field.Get(Value), newField.Get(Value));
                var stateChanged = !SameName(oldWidget.Get(AppearanceState), newWidget.Get(AppearanceState));
                var lookChanged = newWidget.ContainsKey(Appearance) && !SameLook(main, oldWidget, rewritten, newWidget);
                // a widget below its field may carry a value of its own (against the standard, but read): it follows the rewritten one
                var ownValueChanged = fieldId != oldId && !SameValue(oldWidget.Get(Value), newWidget.Get(Value));
                if (valueChanged || stateChanged || lookChanged || ownValueChanged)
                {
                    var widget = Edit(oldId, oldWidget);
                    if (newWidget.GetRaw(AppearanceState) is { } state) widget.Set(AppearanceState, state);
                    else widget.Remove(AppearanceState);
                    if (newWidget.GetRaw(Appearance) is { } look) widget.Set(Appearance, IncrementalParts.Graft(rewritten, update, look, copied));
                    SetValue(widget, fieldId == oldId ? newField : newWidget, rewritten, update, copied);
                }
                if (valueChanged && fieldId != oldId) SetValue(Edit(fieldId, field), newField, rewritten, update, copied);
            }
        }
        foreach (var (id, copy) in edited) update.Replace(id, copy);
    }

    /// <summary>
    /// Whether the normal appearance (/AP /N, a stream or one per state) draws the same: the same states and, for each, the same
    /// decoded content. References cannot be compared, the rewritten file numbers its objects anew.
    /// </summary>
    private static bool SameLook(PdfDocument main, PdfDictionary oldWidget, PdfDocument rewritten, PdfDictionary newWidget)
    {
        var before = oldWidget.GetDictionary(Appearance)?.Get(PdfName.N);
        var after = newWidget.GetDictionary(Appearance)?.Get(PdfName.N);
        return (before, after) switch
        {
            (PdfStream a, PdfStream b) => SameStream(main, a, rewritten, b),
            (PdfDictionary a, PdfDictionary b) when a.Count == b.Count =>
                a.Entries.All(e => a.Get(e.Key) is PdfStream x && b.Get(e.Key) is PdfStream y && SameStream(main, x, rewritten, y)),
            _ => false,
        };
    }

    private static bool SameStream(PdfDocument main, PdfStream a, PdfDocument rewritten, PdfStream b)
    {
        try
        {
            return main.GetStreamData(a).AsSpan().SequenceEqual(rewritten.GetStreamData(b)) && Similar(a.Dictionary.Get(PdfName.Get("BBox")), b.Dictionary.Get(PdfName.Get("BBox")), 0);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException)
        {
            return false;   // unreadable: take the new one
        }
    }

    /// <summary>The widget annotations of a page that are objects of their own (others cannot be replaced on their own).</summary>
    private static List<(PdfObjectId Id, PdfDictionary Dict)> Widgets(PdfArray? annots)
    {
        var list = new List<(PdfObjectId, PdfDictionary)>();
        if (annots is null) return list;
        for (var i = 0; i < Math.Min(annots.Count, 10_000); i++)
            if (annots[i] is PdfReference reference && annots.Get(i) is PdfDictionary annotation && annotation.GetName(PdfName.Subtype) == Widget
                && !ElementStore.HasData(annotation))
                list.Add((reference.Id, annotation));
        return list;
    }

    /// <summary>The node that holds the field's value: the widget if it is the field (has /T), otherwise the nearest named parent.</summary>
    private static (PdfObjectId Id, PdfDictionary Dict) ValueNode(PdfDocument doc, PdfObjectId id, PdfDictionary widget)
    {
        var (nodeId, node) = (id, widget);
        for (var depth = 0; depth < 32 && !node.ContainsKey(PdfName.Get("T")); depth++)
        {
            if (node.GetRaw(PdfName.Parent) is not PdfReference parent || doc.Resolve(parent.Id) is not PdfDictionary up) break;
            (nodeId, node) = (parent.Id, up);
        }
        return node.ContainsKey(PdfName.Get("T")) ? (nodeId, node) : (id, widget);
    }

    private static void SetValue(PdfDictionary target, PdfDictionary from, PdfDocument rewritten, IncrementalUpdate update, Dictionary<PdfObjectId, PdfReference> copied)
    {
        if (from.GetRaw(Value) is { } value) target.Set(Value, IncrementalParts.Graft(rewritten, update, value, copied));
        else target.Remove(Value);
    }

    /// <summary>Values compared as text (strings, names, lists of them), so that the same value written anew counts as unchanged.</summary>
    private static bool SameValue(PdfObject a, PdfObject b) => ValueText(a) == ValueText(b);

    private static string? ValueText(PdfObject value) => value switch
    {
        PdfString s => "s:" + s.ToText(),
        // a signature is the same by its value: what it points to (the catalog in /Reference /Data of a certification) is numbered anew and,
        // since 539, written without /Perms — compared as text it looked replaced (docs/FEHLERGEDAECHTNIS.md, No. 547)
        PdfDictionary signature when PdfSerializer.IsSignatureDictionary(signature) =>
            "sig:" + Convert.ToHexString(signature.GetString(PdfName.Contents)?.Bytes ?? []),
        PdfName n => "n:" + n.Value,
        PdfArray array => "a:" + string.Join('\u0001', array.Take(1000).Select(item => ValueText(item is PdfReference ? PdfNull.Instance : item))),
        PdfNull => null,
        _ => "o:" + value,
    };

    private static bool SameName(PdfObject a, PdfObject b) => (a as PdfName)?.Value == (b as PdfName)?.Value;

    // ------------------------------------------------------------ New own annotations

    /// <summary>
    /// Own annotations (marked with <see cref="ElementStore.Key"/>) that the rewritten file has and the original page has not: copied
    /// into the update and hung onto the page. Matched by description and position, references not compared (they are numbered anew).
    /// </summary>
    /// <returns>How many were added, and those that are fields (empty signature fields), for the form's field list.</returns>
    private static (int Added, List<PdfReference> Fields) NewAnnotations(PdfDocument main, PdfDocument rewritten, IncrementalUpdate update, Dictionary<PdfObjectId, PdfReference> copied)
    {
        var fields = new List<PdfReference>();
        var count = 0;
        for (var i = 0; i < main.PageCount; i++)
        {
            var page = main.Pages[i];
            if (page.Id is not { } pageId) continue;
            var existing = Own(page.Dictionary.GetArray(Annots)).ToList();
            var added = new List<PdfReference>();
            foreach (var (raw, annotation) in OwnWithRefs(rewritten.Pages[i].Dictionary.GetArray(Annots)))
            {
                var match = existing.FindIndex(e => Similar(ElementStore.DataOf(e), ElementStore.DataOf(annotation), 0)
                                                    && Similar(e.Get(Rect), annotation.Get(Rect), 0));
                if (match >= 0)
                {
                    existing.RemoveAt(match);
                    continue;
                }
                if (raw is not PdfReference reference) continue;   // the writer writes every annotation as an object of its own
                var copy = (PdfReference)IncrementalParts.Graft(rewritten, update, reference, copied);
                added.Add(copy);
                if (annotation.GetName(FieldType) is not null) fields.Add(copy);
            }
            if (added.Count > 0) IncrementalParts.AddAnnotations(update, page, pageId, added);
            count += added.Count;
        }
        return (count, fields);
    }

    private static IEnumerable<PdfDictionary> Own(PdfArray? annots) => OwnWithRefs(annots).Select(a => a.Annotation);

    private static IEnumerable<(PdfObject Raw, PdfDictionary Annotation)> OwnWithRefs(PdfArray? annots)
    {
        if (annots is null) yield break;
        for (var i = 0; i < Math.Min(annots.Count, 10_000); i++)
            if (annots.Get(i) is PdfDictionary annotation && ElementStore.HasData(annotation)) yield return (annots[i], annotation);
    }

    /// <summary>Compares written values, any two references counting as equal (the rewritten file numbers its objects anew).</summary>
    private static bool Similar(PdfObject? a, PdfObject? b, int depth)
    {
        if (depth > 16) return true;
        switch (a)
        {
            case null: return b is null;
            case PdfReference: return b is PdfReference;
            case PdfName na: return b is PdfName nb && na == nb;
            case PdfInteger ia: return b is PdfInteger ib && ia.Value == ib.Value || b is PdfReal rb && Math.Abs(ia.Value - rb.Value) < 1e-3;
            case PdfReal ra: return b is PdfReal rb2 && Math.Abs(ra.Value - rb2.Value) < 1e-3 || b is PdfInteger ib2 && Math.Abs(ra.Value - ib2.Value) < 1e-3;
            case PdfBoolean ba: return b is PdfBoolean bb && ba.Value == bb.Value;
            case PdfString sa: return b is PdfString sb && sa.Bytes.AsSpan().SequenceEqual(sb.Bytes);
            case PdfArray aa:
                if (b is not PdfArray ab || aa.Count != ab.Count) return false;
                for (var i = 0; i < aa.Count; i++)
                    if (!Similar(aa[i], ab[i], depth + 1)) return false;
                return true;
            case PdfDictionary da:
                if (b is not PdfDictionary db || da.Count != db.Count) return false;
                foreach (var (key, value) in da.Entries)
                    if (!Similar(value, db.GetRaw(key), depth + 1)) return false;
                return true;
            default:
                return a.GetType() == b?.GetType();
        }
    }

    /// <summary>
    /// New fields into the form's field list, and the form without /NeedAppearances if the rewritten file dropped it (an indirect form or list
    /// is replaced as an object, otherwise the catalog).
    /// </summary>
    /// <returns>true if the catalog itself changed.</returns>
    private static bool UpdateForm(PdfDocument main, IncrementalUpdate update, PdfDictionary catalog, List<PdfReference> added, bool dropNeedAppearances)
    {
        if (added.Count == 0 && !dropNeedAppearances) return false;
        var formRaw = main.Catalog!.GetRaw(AcroForm);
        var form = main.Catalog.GetDictionary(AcroForm) is { } existing ? IncrementalParts.Copy(existing) : new PdfDictionary();
        var formChanged = false;
        if (added.Count > 0)
        {
            var fieldsRaw = form.GetRaw(Fields);
            var fields = form.GetArray(Fields) is { } list ? new PdfArray(list) : new PdfArray();
            foreach (var field in added) fields.Add(field);
            if (fieldsRaw is PdfReference fieldsRef) update.Replace(fieldsRef.Id, fields);
            else
            {
                form.Set(Fields, fields);
                formChanged = true;
            }
        }
        if (dropNeedAppearances)
        {
            form.Remove(NeedAppearances);
            formChanged = true;
        }
        if (!formChanged) return false;
        if (formRaw is PdfReference formRef)
        {
            update.Replace(formRef.Id, form);
            return false;
        }
        catalog.Set(AcroForm, form);
        return true;
    }
}

/// <summary>The changes cannot be appended to the signed file without changing more than allowed; nothing was written.</summary>
/// <param name="message">The localised message with the reason.</param>
public sealed class NotAppendableException(string message) : PdfException(message);
