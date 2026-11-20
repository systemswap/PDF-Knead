using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>File → New, and blank pages in the chosen format.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Lets the user choose a format (set by the view): takes the preset and whether pages are inserted rather than a new document created; returns the choice, or null if cancelled.</summary>
    public Func<PageSetup, bool, Task<BlankPagesChoice?>>? PickPageSetup { get; set; }

    /// <summary>The format chosen last; the page count always starts at 1.</summary>
    private static PageSetup RememberedSetup => (UserSettings.Current.NewPage ?? new PageSetup()).Checked() with { Count = 1 };

    private static void Remember(PageSetup setup) => UserSettings.Update(s => s with { NewPage = setup with { Count = 1 } });

    /// <summary>File → New (Ctrl+N): choose a format, then open an empty document in a new tab.</summary>
    [RelayCommand]
    private async Task NewDocument()
    {
        if (!EnsureAllowed("start.new")) return;
        var setup = RememberedSetup;
        string? title = null, language = null;
        if (PickPageSetup is not null)
        {
            if (await PickPageSetup(setup, false) is not { } choice) return;
            setup = choice.Setup;
            (title, language) = (choice.Title, choice.Language);
            Remember(setup);
        }
        CreateDocument(setup, title, language);
    }

    /// <summary>Creates an empty document „Unbenannt n“ in memory; it is written to disk only on the first save.</summary>
    /// <param name="setup">Page format and number of pages.</param>
    /// <param name="title">The title of the document (for screen readers); null: none.</param>
    /// <param name="language">The language of the document; null: the language of the program (docs/FEHLERGEDAECHTNIS.md, No. 492).</param>
    /// <returns>The new document, already selected.</returns>
    public DocumentViewModel CreateDocument(PageSetup setup, string? title = null, string? language = null)
    {
        var (w, h) = setup.Points;
        var blank = BlankPage.CreateDocument(w, h, setup.Pages, "1.7", title, language ?? Tr.Culture.Name);
        var doc = new DocumentViewModel(UntitledName(), blank, untitled: true) { Tool = Tool };
        Documents.Add(doc);
        SelectedDocument = doc;
        BackstageOpen = false;
        return doc;
    }

    /// <summary>„Unbenannt 1.pdf“, „Unbenannt 2.pdf“ …: the lowest number not used by an open untitled tab.</summary>
    private string UntitledName()
    {
        for (var n = 1; ; n++)
        {
            var name = Tr.T("Unbenannt {0}", n) + ".pdf";
            if (!Documents.Any(d => d.IsUntitled && d.FileName == name)) return name;
        }
    }

    /// <summary>Saves a new document: the location is chosen first (as with „Speichern unter“, starting in the Documents folder).</summary>
    private async Task<bool> SaveUntitledAsync(DocumentViewModel doc)
    {
        if (PickSavePath is null || !await RedactFirstAsync(doc, Tr.T("Speichern unter"))) return false;
        var path = await PickSavePath(Tr.T("Speichern unter"), doc.FileName, SaveFolder(doc));
        return path is not null && await SaveDocumentAsync(doc, path, doc.SaveVersion);
    }

    /// <summary>Folder in which the save dialogue starts: the file's folder, or Documents for new documents.</summary>
    private static string SaveFolder(DocumentViewModel doc) =>
        doc.IsUntitled ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : doc.Folder;

    /// <summary>„Leerseite“ menu: inserts a blank page in the given format („A4“, „A3 quer“) after the current one.</summary>
    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private void InsertBlankPageAs(string? format)
    {
        if (SelectedDocument is { } doc && Editable(doc) && PageSetup.Parse(format) is { } setup)
            doc.InsertBlankPages(setup, BlankPagePosition.After);
    }

    /// <summary>„Leerseite“ → „Weitere …“ menu: format, count and position are chosen in a window.</summary>
    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private async Task InsertBlankPages()
    {
        if (SelectedDocument is not { } doc || PickPageSetup is null || !Editable(doc)) return;
        if (await PickPageSetup(RememberedSetup, true) is not { } choice || !Documents.Contains(doc)) return;
        Remember(choice.Setup);
        doc.InsertBlankPages(choice.Setup, choice.Position);
    }
}
