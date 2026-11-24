using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.ViewModels;

/// <summary>A note in the area „Notizen“.</summary>
/// <param name="note">The note.</param>
public sealed class NoteRow(PageNote note)
{
    /// <summary>The note.</summary>
    public PageNote Note { get; } = note;

    /// <summary>„Seite 2 · Name · 30.10.2026 10:05“.</summary>
    public string Header => string.Join(" · ", new[] { Tr.T("Seite {0}", Note.Page + 1), Note.Author, When(Note.Date) }.Where(p => p is { Length: > 0 }));

    /// <summary>About what; may be empty.</summary>
    public string Subject => Note.Subject ?? "";

    /// <summary>The text; a note without one says so.</summary>
    public string Text => Note.Text is { Length: > 0 } text ? text : Tr.T("(ohne Text)");

    /// <summary>The answers, each with who and when; may be empty.</summary>
    public string Replies => string.Join("\n\n", Note.Replies.Select(r =>
        string.Join(" · ", new[] { r.Author, When(r.Date) }.Where(p => p is { Length: > 0 })) is { Length: > 0 } who ? who + "\n" + r.Text : r.Text));

    /// <summary>Whether it has answers.</summary>
    public bool HasReplies => Note.Replies.Count > 0;

    /// <summary>A note of PDF Knead: its text can be changed in the area, and it can be deleted.</summary>
    public bool IsOwn => Note.Own is not null;

    private static string? When(DateTimeOffset? date) => date?.ToLocalTime().ToString("g", Tr.Culture);
}

/// <summary>
/// „Notizen“ as an area on the right (KONZEPT.md, section 5.5a; wish of 30.10.2026): all notes of the document to read; a line jumps to its note and
/// frames it, a click on a note symbol on the page opens the area with that note. Notes of other programs are only read; own notes (Einfügen →
/// „Notiz“) are written and changed there and can be deleted.
/// </summary>
public sealed partial class DocumentViewModel
{
    private bool _choosingNoteQuietly;

    /// <summary>Whether the area „Notizen“ is open.</summary>
    [ObservableProperty]
    public partial bool IsNotesOpen { get; private set; }

    /// <summary>The notes of the document.</summary>
    public ObservableCollection<NoteRow> NoteRows { get; } = [];

    /// <summary>The note chosen.</summary>
    [ObservableProperty]
    public partial NoteRow? SelectedNoteRow { get; set; }

    /// <summary>Whether the document has no notes.</summary>
    public bool NotesNothing => NoteRows.Count == 0;

    /// <summary>„3 Notizen“.</summary>
    public string NotesCountText => NoteRows.Count == 1 ? Tr.T("1 Notiz") : Tr.T("{0} Notizen", NoteRows.Count);

    /// <summary>Opens the area; one area at a time on the right.</summary>
    public void OpenNotes()
    {
        if (IsCleanupOpen) CloseCleanup();
        if (IsFieldProposalsOpen) CloseFieldProposals();
        if (IsAccessibilityOpen) CloseAccessibility();
        IsNotesOpen = true;
        FillNotes();
    }

    /// <summary>Closes the area; the frame goes.</summary>
    public void CloseNotes()
    {
        IsNotesOpen = false;
        SelectedNoteRow = null;
        NoteRows.Clear();
        OnNotesChanged();
    }

    /// <summary>The notes as they now stand, keeping the note chosen (moved, it is a new element of the same annotation).</summary>
    private void FillNotes()
    {
        var chosen = SelectedNoteRow?.Note.Annotation;
        var chosenOwn = _chosenOwn ?? SelectedNoteRow?.Note.Own;
        _chosenOwn = null;
        _choosingNoteQuietly = true;
        SelectedNoteRow = null;
        NoteRows.Clear();
        for (var page = 0; page < History.Pages.Count; page++)
            foreach (var note in PageNotes.Of(page, History.Pages[page]))
                NoteRows.Add(new NoteRow(note));
        if (chosen is not null) SelectedNoteRow = NoteRows.FirstOrDefault(r => ReferenceEquals(r.Note.Annotation, chosen));
        else if (chosenOwn is not null) SelectedNoteRow = NoteRows.FirstOrDefault(r => ReferenceEquals(r.Note.Own, chosenOwn));
        _choosingNoteQuietly = false;
        if ((chosen is not null || chosenOwn is not null) && SelectedNoteRow is { } again) ShowNotePlace(again, reveal: false);
        else if (chosen is not null || chosenOwn is not null) ClearShownPlace();
        OnNotesChanged();
        OnPropertyChanged(nameof(NoteText));
        OnPropertyChanged(nameof(NoteAuthor));
    }

    /// <summary>After a change of the document: a note may have gone, moved or been added.</summary>
    private void NotesHistoryChanged()
    {
        if (IsNotesOpen) FillNotes();
    }

    private void OnNotesChanged()
    {
        OnPropertyChanged(nameof(NotesNothing));
        OnPropertyChanged(nameof(NotesCountText));
    }

    partial void OnSelectedNoteRowChanged(NoteRow? value)
    {
        OnPropertyChanged(nameof(NoteText));
        OnPropertyChanged(nameof(NoteAuthor));
        if (_choosingNoteQuietly) return;
        if (value is null) ClearShownPlace();
        else ShowNotePlace(value, reveal: true);
    }

    private void ShowNotePlace(NoteRow row, bool reveal)
    {
        var rect = row.Note.Rect;
        ShownPlace = (row.Note.Page, [rect], null);
        HiddenSpotsChanged?.Invoke();
        if (!reveal) return;
        ScrollToPage(row.Note.Page);
        RevealRequested?.Invoke(row.Note.Page, new TextQuad(rect.Left, rect.Top, rect.Right, rect.Top, rect.Right, rect.Bottom, rect.Left, rect.Bottom));
    }

    /// <summary>A note chosen as an element of the page.</summary>
    private void NoteClicked(ElementRef? element)
    {
        if (element is { Element: ForeignAnnotation { Kind: "Text" } foreign }) ChooseNote(r => ReferenceEquals(r.Note.Annotation, foreign.Original));
        else if (element is { Element: NoteElement own }) ChooseNote(r => ReferenceEquals(r.Note.Own, own));
    }

    /// <summary>
    /// A click on the page: a note symbol there shows its note. Notes without an appearance of their own are no elements, so nothing else finds them.
    /// </summary>
    /// <param name="page">The page (index).</param>
    /// <param name="x">Position on the page.</param>
    /// <param name="y">Position on the page.</param>
    /// <returns>Whether there was a note.</returns>
    public bool ChooseNoteAt(int page, double x, double y, double noZoomScale = 1)
    {
        if ((uint)page >= (uint)History.Pages.Count) return false;
        var note = PageNotes.Of(page, History.Pages[page]).LastOrDefault(n => n.Kind == "Text" && Shown(n) is var r && x >= r.Left && x <= r.Right && y >= r.Bottom && y <= r.Top);
        return note is not null && ChooseNote(r => note.Own is { } own ? ReferenceEquals(r.Note.Own, own) : ReferenceEquals(r.Note.Annotation, note.Annotation));

        // where a note symbol is shown: a text note behaves as if NoZoom were set (ISO 32000-2, 12.5.3, 12.5.6.4) — above 100 % smaller,
        // anchored at the top-left corner of /Rect; own notes as well (docs/FEHLERGEDAECHTNIS.md, No. 576, 577)
        PdfRectangle Shown(PageNote n) =>
            noZoomScale < 1
                ? new PdfRectangle(n.Rect.Left, n.Rect.Top - n.Rect.Height * noZoomScale, n.Rect.Left + n.Rect.Width * noZoomScale, n.Rect.Top)
                : n.Rect;
    }

    /// <summary>Shows a note in the area (without scrolling: it is in view already). Not while a tool has an area of its own.</summary>
    private bool ChooseNote(Func<NoteRow, bool> which)
    {
        if (IsFormDesign || Tool is not (ViewTool.SelectText or ViewTool.Pan)) return false;
        if (!IsNotesOpen) OpenNotes();
        if (NoteRows.FirstOrDefault(which) is not { } row) return false;
        _choosingNoteQuietly = true;
        SelectedNoteRow = row;
        _choosingNoteQuietly = false;
        ShowNotePlace(row, reveal: false);
        return true;
    }

    // ------------------------------------------------------------ Own notes (Einfügen → „Notiz“)

    /// <summary>The own note to choose when the list is filled next (it is a new element after a change).</summary>
    private NoteElement? _chosenOwn;

    /// <summary>The name for new notes: the one used last in this session (nothing is filled in by itself).</summary>
    public static string LastNoteAuthor { get; set; } = "";

    /// <summary>Raised when a new note waits for its text (the area puts the cursor into it).</summary>
    public event Action? NoteTextRequested;

    /// <summary>
    /// Places a new note with its tip at a point of the page (the bubble above and right of it, upright as the page is shown) and opens the area
    /// with it, ready for its text. One undo step.
    /// </summary>
    /// <param name="page">The page (index).</param>
    /// <param name="x">Point on the page.</param>
    /// <param name="y">Point on the page.</param>
    /// <returns>The note; null if the document allows no changes.</returns>
    public NoteElement? AddNote(int page, double x, double y)
    {
        if (!CanAnnotate || (uint)page >= (uint)History.Count) return null;
        var s = NoteElement.Size;
        var note = new NoteElement { Rect = new PdfRectangle(x, y, x + s, y + s), Rotation = DisplayRotation(page), Author = LastNoteAuthor };
        History.AddElement(page, note, Tr.T("Notiz einfügen"));
        _chosenOwn = note;
        if (!IsNotesOpen) OpenNotes();
        else FillNotes();
        NoteTextRequested?.Invoke();
        return note;
    }

    /// <summary>The text of the chosen own note; changing it is one undo step (null for notes of other programs).</summary>
    public string? NoteText
    {
        get => SelectedNoteRow?.Note.Own?.Text;
        set => ChangeOwnNote(n => value is null || value == n.Text ? n : n with { Text = value, Modified = DateTimeOffset.Now }, Tr.T("Notiz ändern"));
    }

    /// <summary>Who wrote the chosen own note; the name is also taken for the next notes of this session.</summary>
    public string? NoteAuthor
    {
        get => SelectedNoteRow?.Note.Own?.Author;
        set
        {
            var author = (value ?? "").Trim();
            if (SelectedNoteRow?.Note.Own is null) return;
            LastNoteAuthor = author;
            ChangeOwnNote(n => author == n.Author ? n : n with { Author = author, Modified = DateTimeOffset.Now }, Tr.T("Notiz ändern"));
        }
    }

    private void ChangeOwnNote(Func<NoteElement, NoteElement> change, string description)
    {
        if (SelectedNoteRow?.Note is not { Own: { } own } note || !CanAnnotate) return;
        var updated = change(own);
        if (ReferenceEquals(updated, own)) return;
        _chosenOwn = updated;
        if (!History.ReplaceElement(note.Page, own, updated, description)) _chosenOwn = null;
    }

    /// <summary>Deletes the chosen own note (one undo step).</summary>
    [RelayCommand]
    private void DeleteNote()
    {
        if (SelectedNoteRow?.Note is not { Own: { } own } note || !CanAnnotate) return;
        History.RemoveElements(note.Page, [own], Tr.T("Notiz löschen"));
    }
}
