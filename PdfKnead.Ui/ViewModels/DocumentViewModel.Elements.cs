using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Security;

namespace PdfKnead.ViewModels;

/// <summary>An element on a page (<see cref="Page"/> is the position in the page list).</summary>
public readonly record struct ElementRef(int Page, PageElement Element);

/// <summary>Elements on the pages: select, insert, change, delete; every step can be undone.</summary>
public sealed partial class DocumentViewModel
{
    /// <summary>The selected element (frame with handles); null = none.</summary>
    [ObservableProperty] public partial ElementRef? SelectedElement { get; set; }

    /// <summary>
    /// Whether the selected redaction mark was picked by a click in a redaction tool (not just drawn): then its panel with the reason
    /// shows there too, without leaving the tool first (wish of 4.11.2026). A mark just drawn stays without it, so that the panel does
    /// not cover the page where the next one is drawn.
    /// </summary>
    [ObservableProperty] public partial bool MarkPicked { get; set; }

    /// <summary>
    /// The element whose text is being edited in the input box; the view does not draw it meanwhile.
    /// Newly drawn elements are added to the page only once they have text.
    /// </summary>
    [ObservableProperty] public partial ElementRef? EditingElement { get; private set; }

    /// <summary>Whether the edited element is new (not yet on the page).</summary>
    public bool EditingIsNew { get; private set; }

    // ------------------------------------------------------------ Permissions (ISO 32000-2, 7.6.4.2, Table 22)
    // Each action asks for the right of what it writes into the file; with the owner password, without encryption or after
    // „Einschränkungen entfernen“ everything is allowed (HasAllRights). docs/FEHLERGEDAECHTNIS.md, No. 1111.

    /// <summary>
    /// Whether a bit of /P is granted. Bits 9 to 12 have a meaning only from revision 3 (Table 22): with revision 2 they never count, whatever
    /// the file sets.
    /// </summary>
    private bool Granted(PdfPermissions bit) =>
        Document.Security is { } s && s.Permissions.HasFlag(bit) && (s.Revision >= 3 || (bit & FromRevision3) == 0);

    /// <summary>Bits 9 to 12 of /P, which revision 2 does not know.</summary>
    private const PdfPermissions FromRevision3 = PdfPermissions.FillForms | PdfPermissions.Accessibility | PdfPermissions.Assemble | PdfPermissions.PrintHighQuality;

    /// <summary>
    /// Bit 4 („Ändern“): changing the document other than by annotations, form fields and the page sequence — edited text and pictures of the
    /// page content, layers of the content, the scale of a page, signatures fixed in the page.
    /// </summary>
    public bool CanModifyContent => HasAllRights || Granted(PdfPermissions.Modify);

    /// <summary>
    /// Bit 6 („Kommentieren“): adding and changing annotations — notes, own texts, shapes, images and codes (saved as annotations, KONZEPT.md,
    /// section 5.13), dimensions, annotations of other programs.
    /// </summary>
    public bool CanAnnotate => HasAllRights || Granted(PdfPermissions.Annotate);

    /// <summary>Bits 6 and 4 together: creating and changing form fields, signature fields included (Table 22, bit 6).</summary>
    public bool CanDesignForms => CanAnnotate && CanModifyContent;

    /// <summary>
    /// Redaction marks: saved as annotations (bit 6), and applying them — also implicitly when saving as a new file — removes page content
    /// (bit 4); so both are needed.
    /// </summary>
    public bool CanRedact => CanAnnotate && CanModifyContent;

    /// <summary>
    /// Collective name: whether any element may be placed at all (annotations or page content). Every action asks for its own right —
    /// <see cref="CanChange"/>, <see cref="CanModifyContent"/>, <see cref="CanAnnotate"/>, <see cref="CanDesignForms"/> — never for this one.
    /// </summary>
    public bool CanEditElements => CanModifyContent || CanAnnotate;

    /// <summary>
    /// Whether this element may be added, changed, moved or deleted, by what it is written as: a digital signature into a signature field the
    /// file has fills that field (<see cref="CanFillForms"/>), elsewhere it creates a new signature field (<see cref="CanDesignForms"/>); a
    /// redaction mark needs <see cref="CanRedact"/>; form fields <see cref="CanDesignForms"/>; elements saved as annotations
    /// <see cref="CanAnnotate"/>; everything fixed in the page content (edited text, signatures, freehand drawings, sealed elements)
    /// <see cref="CanModifyContent"/>.
    /// </summary>
    /// <param name="element">The element (for a replacement: ask for the old one and the new one).</param>
    public bool CanChange(PageElement element) => element switch
    {
        { Digital.FieldName.Length: > 0 } => CanFillForms,
        { Digital: not null } => CanDesignForms,
        RedactionElement => CanRedact,
        _ when IsFormField(element) => CanDesignForms,
        _ when ElementStore.StaysEditable(element) => CanAnnotate,
        _ => CanModifyContent,
    };

    /// <summary>
    /// Whether a placing tool may start, by what it places: fields <see cref="CanDesignForms"/>, redaction marks <see cref="CanRedact"/>,
    /// texts, images, shapes, codes and notes <see cref="CanAnnotate"/>. A signature is fixed in the page or fills a signature field, so
    /// either right lets the tool start; where it goes decides (<see cref="CanChange"/>).
    /// </summary>
    /// <param name="tool">The tool.</param>
    public bool CanPlace(ViewTool tool) => tool switch
    {
        ViewTool.InsertField or ViewTool.InsertSignatureField or ViewTool.InsertFormField or ViewTool.DesignForm => CanDesignForms,
        ViewTool.InsertRedaction or ViewTool.RedactText or ViewTool.RedactHidden => CanRedact,
        ViewTool.InsertSignature => CanModifyContent || CanFillForms,
        _ => CanAnnotate,
    };

    /// <summary>
    /// Whether the „Text“ panel may change something: the selected text element by what it is (<see cref="CanChange"/>), otherwise the
    /// next text of the „Text“ tool (an annotation).
    /// </summary>
    public bool CanEditTextFormat => SelectedElement is { } selected ? CanChange(selected.Element) : CanAnnotate;

    /// <summary>A message for the bar below the ribbon (the main window shows it).</summary>
    public event Action<string>? MessageRequested;

    /// <summary>Raises <see cref="MessageRequested"/>.</summary>
    /// <param name="message">The message to show.</param>
    public void Notify(string message) => MessageRequested?.Invoke(message);

    /// <summary>Raised before a question opens while the mouse may be pressed: the view lets go of any drag, as the release will go to the dialog.</summary>
    public event Action? DragCancelRequested;

    /// <summary>Asks the view to cancel a drag in progress (before a modal question; docs/FEHLERGEDAECHTNIS.md, No. 397).</summary>
    public void CancelDrag() => DragCancelRequested?.Invoke();

    /// <summary>Double-click on a signature: choose the name or handwriting again (the main window asks).</summary>
    public event Action<ElementRef>? SignatureEditRequested;

    /// <summary>Raises <see cref="SignatureEditRequested"/>.</summary>
    /// <param name="signature">The signature to edit.</param>
    public void RequestSignatureEdit(ElementRef signature) => SignatureEditRequested?.Invoke(signature);

    /// <summary>
    /// Flatten on save: fields, comments and stamps become part of the page content; afterwards nothing
    /// can be filled in or moved any more. Applies until the next save (counts as a modification).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified), nameof(Title))]
    public partial bool FlattenOnSave { get; set; }

    /// <summary>
    /// Whether the document has anything to flatten (form fields, comments, own text fields) — signature fields do not count (a signature
    /// is not filled in afterwards), and a file with intact digital signatures has nothing to offer: flattening would rewrite it and break them.
    /// </summary>
    public bool HasFlattenableContent
    {
        get
        {
            if (!IsUntitled && Core.Pdf.Signatures.DigitalSignatures.CountIntact(Document) > 0) return false;
            var sig = Core.Pdf.Objects.PdfName.Get("Sig");
            foreach (var item in History.Pages)
            {
                if (item.Elements.Any(e => e is TextElement { IsField: true })) return true;
                if (item.Page.Dictionary.Get(Core.Pdf.Objects.PdfName.Get("Annots")) is not Core.Pdf.Objects.PdfArray annots) continue;
                for (var i = 0; i < annots.Count; i++)
                    if (annots.Get(i) is Core.Pdf.Objects.PdfDictionary annot && annot.GetName(Core.Pdf.Objects.PdfName.Subtype)?.Value is not ("Link" or "Popup")
                        && (annot.GetName(Core.Pdf.Objects.PdfName.Get("FT")) ?? annot.GetDictionary(Core.Pdf.Objects.PdfName.Parent)?.GetName(Core.Pdf.Objects.PdfName.Get("FT"))) != sig)
                        return true;
            }
            return false;
        }
    }

    /// <summary>Raised once after signing to ask whether to flatten (only if there are fields or comments).</summary>
    public event Action? FlattenSuggested;

    private bool _flattenAsked;

    /// <summary>The elements of a page in their current state.</summary>
    /// <param name="page">Page index.</param>
    /// <returns>The elements; empty for an index outside the document.</returns>
    public IReadOnlyList<PageElement> ElementsOn(int page) =>
        page >= 0 && page < History.Count ? History.Pages[page].Elements : [];

    /// <summary>
    /// After undo and similar steps: clears the selection if the element no longer exists. While an element is being replaced, the
    /// selection moves straight to its successor — cleared for a moment, the properties panel would vanish and its text box lose the
    /// focus with every typed character.
    /// </summary>
    private void ValidateElementSelection()
    {
        if (SelectedElement is { } s && !ElementsOn(s.Page).Contains(s.Element))
            SelectedElement = _replacing is { } r && ReferenceEquals(r.Old, s.Element) ? r.New : null;
        if (EditingElement is { } e && !EditingIsNew && !ElementsOn(e.Page).Contains(e.Element)) EditingElement = null;
        ValidateRichEditor();
        ValidateFieldSelection();
    }

    /// <summary>Adds an element to a page (one undo step) and selects it.</summary>
    /// <param name="page">Page index.</param>
    /// <param name="element">The new element.</param>
    /// <param name="description">Name of the undo step.</param>
    public void AddElement(int page, PageElement element, string description)
    {
        History.AddElement(page, element, description);
        MarkPicked = false;
        SelectedElement = new ElementRef(page, element);
        SuggestFlatten(element);
        CheckPageEdge(new ElementRef(page, element));   // beyond the edge of the page (No. 622)
    }

    /// <summary>Signs into an own signature field: the field becomes the signature (one step).</summary>
    /// <param name="field">The signature field.</param>
    /// <param name="signature">The signature that replaces it.</param>
    public void SignInto(ElementRef field, PageElement signature)
    {
        if (!History.ReplaceElement(field.Page, field.Element, signature, Tr.T("Unterschrift einfügen"))) return;
        SelectedElement = new ElementRef(field.Page, signature);
        SuggestFlatten(signature);
    }

    /// <summary>After signing, if there is still something fillable: offers once to flatten on save.</summary>
    private void SuggestFlatten(PageElement element)
    {
        if (element is not (SignatureElement or InkElement) && element.Seal is null) return;
        if (_flattenAsked || FlattenOnSave || !HasFlattenableContent) return;
        _flattenAsked = true;
        FlattenSuggested?.Invoke();
    }

    /// <summary>
    /// Finds the signature field at a spot on the page: an own one (element) or one of the file
    /// (widget with /FT /Sig, also inherited).
    /// </summary>
    /// <param name="page">Page index.</param>
    /// <param name="x">X coordinate in page space.</param>
    /// <param name="y">Y coordinate in page space.</param>
    /// <returns>Its rectangle and, for an own field, the element, for a field of the file its full name; null if there is none.</returns>
    public (PdfRectangle Rect, SignatureFieldElement? Own, string? FieldName)? SignatureFieldAt(int page, double x, double y)
    {
        if (page < 0 || page >= History.Count) return null;
        var elements = ElementsOn(page);
        for (var i = elements.Count - 1; i >= 0; i--)
            if (elements[i] is SignatureFieldElement own && Contains(own.Rect, x, y)) return (own.Rect, own, null);
        var item = History.Pages[page];
        if (item.Page.Dictionary.Get(Core.Pdf.Objects.PdfName.Get("Annots")) is not Core.Pdf.Objects.PdfArray annots) return null;
        for (var i = 0; i < annots.Count; i++)
        {
            if (annots.Get(i) is not Core.Pdf.Objects.PdfDictionary annot || annot.GetName(Core.Pdf.Objects.PdfName.Subtype)?.Value != "Widget") continue;
            var type = Core.Pdf.Signatures.DigitalSignatures.InheritedFieldType(annot)?.Value;   // inherited in any depth (12.7.4.1)
            if (type != "Sig" || PdfRectangle.From(annot.Get(Core.Pdf.Objects.PdfName.Get("Rect"))) is not { } rect) continue;
            // moved or deleted as a foreign element: its current position counts
            if (ForeignAnnotations.IsElement(item.Source, item.Page, annot))
            {
                if (elements.OfType<ForeignAnnotation>().FirstOrDefault(f => ReferenceEquals(f.Original, annot)) is not { } foreign) continue;
                rect = foreign.Rect;
            }
            if (Contains(rect, x, y)) return (rect, null, FullFieldName(annot));
        }
        return null;
    }

    /// <summary>
    /// What a prepared signature field of the file asks for, said when a digital signature is placed into it (docs/ABLAUFPLAN.md, 2.3): the
    /// fields its /Lock locks after signing, and its seed values (/SV) — what cannot be met (signing into the field will be refused) and what
    /// is only recommended. The certificate is not known yet here; signing checks it. Null if there is nothing to say.
    /// </summary>
    /// <param name="fieldName">Full name of the field.</param>
    /// <param name="certify">The certification asked for in the signature.</param>
    public string? PreparedFieldNotice(string fieldName, Core.Pdf.Signatures.CertificationLevel? certify)
    {
        var settings = Services.UserSettings.Current;
        return Core.Pdf.Signatures.SeedValues.Notice(Document, fieldName, Core.Pdf.Signatures.SigningPlan.Of(
            settings.SignatureTimestamps && !string.IsNullOrWhiteSpace(settings.TimestampServer), certify,
            Core.Pdf.Signatures.DigitalSignatures.Find(Document).Count == 0, null));
    }

    /// <summary>Full name of a field (the partial names /T up the /Parent chain, joined with dots), as a digital signature names it.</summary>
    private static string? FullFieldName(Core.Pdf.Objects.PdfDictionary field)
    {
        var parts = new List<string>();
        for (var (node, depth) = (field, 0); node is not null && depth < 32; node = node.GetDictionary(Core.Pdf.Objects.PdfName.Parent), depth++)
            if (node.GetString(Core.Pdf.Objects.PdfName.Get("T")) is { } title) parts.Insert(0, title.ToText());
        return parts.Count == 0 ? null : string.Join('.', parts);
    }

    private static bool Contains(PdfRectangle r, double x, double y) =>
        x >= Math.Min(r.Left, r.Right) && x <= Math.Max(r.Left, r.Right) && y >= Math.Min(r.Bottom, r.Top) && y <= Math.Max(r.Bottom, r.Top);

    /// <summary>Replaces an element (changed, moved …) and keeps it selected.</summary>
    /// <param name="old">The element to replace.</param>
    /// <param name="updated">The new version.</param>
    /// <param name="description">Name of the undo step.</param>
    /// <param name="continueLast">Merge with the last step of the same kind (arrow keys).</param>
    /// <returns>false if nothing changed.</returns>
    public bool ReplaceElement(ElementRef old, PageElement updated, string description, bool continueLast = false)
    {
        if (ReferenceEquals(old.Element, updated)) return false;
        // an annotation its author locked: asked first; the change follows only with „Trotzdem ändern“ (No. 541)
        if (LockedAsks(old.Element, updated, () => ReplaceElement(old, updated, description, continueLast))) return false;
        _replacing = (old.Element, new ElementRef(old.Page, updated));
        try
        {
            if (!History.ReplaceElement(old.Page, old.Element, updated, description, continueLast)) return false;
        }
        finally
        {
            _replacing = null;
        }
        SelectedElement = new ElementRef(old.Page, updated);
        // beyond the edge of the page (No. 622) — not while nudged with the arrow keys or a slider (continueLast)
        if (!continueLast) CheckPageEdge(new ElementRef(old.Page, updated));
        return true;
    }

    // the element being replaced and its successor (see ValidateElementSelection)
    private (PageElement Old, ElementRef New)? _replacing;

    /// <summary>
    /// Asks whether to change an annotation its author locked anyway (set by the main window's model; null: no question, as without a view).
    /// The argument is the question; true for „Trotzdem ändern“.
    /// </summary>
    public Func<string, Task<bool>>? ConfirmLockedChange { get; set; }

    // annotations the user allowed to change in this session (by their dictionary in the file)
    private readonly HashSet<Core.Pdf.Objects.PdfDictionary> _lockedAllowed = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Whether a change of a foreign annotation its author locked must be asked first (ISO 32000-2, 12.5.3, Table 167; docs/ABLAUFPLAN.md,
    /// 4.4 a; docs/FEHLERGEDAECHTNIS.md, No. 541): Locked — deleting, moving, resizing, changed properties (the field value may change);
    /// LockedContents — changed properties or value. Then the question is put and the change runs after „Trotzdem ändern“; with „Abbrechen“
    /// everything stays. Widgets of signature fields are left to the warning about signatures (KONZEPT §5.17, S0). Redacting, cleaning
    /// up and flattening do not come through here (data protection before the lock).
    /// </summary>
    /// <param name="old">The element as it is.</param>
    /// <param name="updated">The element as it is to be; null to delete it.</param>
    /// <param name="then">The change, run after „Trotzdem ändern“.</param>
    /// <returns>True if the change waits for the answer (the caller does nothing now).</returns>
    private bool LockedAsks(PageElement old, PageElement? updated, Action then)
    {
        if (old is not ForeignAnnotation foreign || ConfirmLockedChange is not { } confirm || _lockedAllowed.Contains(foreign.Original)) return false;
        if (IsSignatureWidget(foreign)) return false;
        var changed = updated as ForeignAnnotation;
        var asks = foreign.IsLocked && (updated is null || changed is null || changed.Rect != foreign.Rect || changed.Spec != foreign.Spec)
                   || foreign.IsContentLocked && changed is not null && (changed.Spec != foreign.Spec || changed.Value != foreign.Value);
        if (!asks) return false;
        _ = AskLockedAsync(confirm, foreign, then);
        return true;
    }

    private async Task AskLockedAsync(Func<string, Task<bool>> confirm, ForeignAnnotation foreign, Action then)
    {
        if (!await confirm(Tr.T("Diese Anmerkung ist vom Ersteller gesperrt. Trotzdem ändern?"))) return;
        _lockedAllowed.Add(foreign.Original);
        then();
    }

    /// <summary>Whether the annotation is the widget of a signature field (its /FT, inherited up the /Parent chain, is /Sig).</summary>
    private static bool IsSignatureWidget(ForeignAnnotation foreign)
    {
        if (foreign.Kind != "Widget") return false;
        for (var (node, depth) = (foreign.Original, 0); node is not null && depth < 32; node = node.GetDictionary(Core.Pdf.Objects.PdfName.Parent), depth++)
            if (node.GetName(Core.Pdf.Objects.PdfName.Get("FT"))?.Value is { } type) return type == "Sig";
        return false;
    }

    /// <summary>Deletes the selected element (one undo step).</summary>
    /// <returns>false if nothing was deleted.</returns>
    public bool DeleteSelectedElement()
    {
        if (SelectedElement is not { } s) return false;
        if (LockedAsks(s.Element, null, () =>
            {
                SelectedElement = s;
                DeleteSelectedElement();
            }))
            return false;
        SelectedElement = null;
        return History.RemoveElements(s.Page, [s.Element], Tr.T("Element löschen"));
    }

    /// <summary>Starts editing text (of a newly drawn or an existing element).</summary>
    /// <param name="element">The element.</param>
    /// <param name="isNew">true = newly drawn, not yet on the page.</param>
    public void BeginEdit(ElementRef element, bool isNew)
    {
        EditingIsNew = isNew;
        EditingElement = element;
        SelectedElement = isNew ? null : element;
    }

    /// <summary>Asks the view to take over the text from its input box (it calls <see cref="EndEdit"/>).</summary>
    public event Action? CommitEditRequested;

    /// <summary>
    /// Takes over pending text input before saving and closing; otherwise the text just typed would be
    /// missing (the input box keeps the focus on Ctrl+S).
    /// </summary>
    public void CommitPendingEdit()
    {
        if (EditingElement is not null) CommitEditRequested?.Invoke();
        // Edited page text: end the edit; a block that was only clicked and not changed is restored and not saved in converted form
        EndRichEdit();
    }

    /// <summary>
    /// Ends editing: the new text goes into the page (one step). A new element without text
    /// disappears again; an existing one that becomes empty is deleted.
    /// </summary>
    /// <param name="text">The edited text.</param>
    public void EndEdit(string text)
    {
        if (EditingElement is not { } editing) return;
        var isNew = EditingIsNew;
        EditingIsNew = false;
        if (FillingField is { } filling)
        {
            // A field created by another program was filled in: the value belongs to the field, not to the stand-in text
            FillingField = null;
            EditingElement = null;
            EndFill(filling, text);
            return;
        }
        EditingElement = null;
        if (editing.Element is not TextElement element) return;
        var fitted = ElementAppearance.FitHeight(element with { Text = text });
        // Label of a check box: width fitted to the text; side and gap stay
        if (!isNew && IsFieldLabel(element) && FieldOfLabel(editing) is { } box)
        {
            var (left, gap) = LabelSide(element, box.Element);
            fitted = PlaceLabel(element with { Text = text.ReplaceLineEndings(" ") }, box.Element, left, gap);
        }
        if (isNew)
        {
            if (text.Trim().Length > 0) AddElement(editing.Page, fitted, element.IsField ? Tr.T("Textfeld einfügen") : Tr.T("Text einfügen"));
            return;
        }
        if (text.Trim().Length == 0)
        {
            SelectedElement = editing;
            DeleteSelectedElement();
            return;
        }
        if (text != element.Text) ReplaceElement(editing, fitted, Tr.T("Text ändern"));
        else SelectedElement = editing;
    }
}
