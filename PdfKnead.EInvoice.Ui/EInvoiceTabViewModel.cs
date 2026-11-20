using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf;
using PdfKnead.Modules;
using PdfKnead.ViewModels;

namespace PdfKnead.EInvoice.Ui;

/// <summary>What the group „Info“ of the tab says about the selected document.</summary>
public enum InvoiceInfoState
{
    /// <summary>No document.</summary>
    None,

    /// <summary>Being searched.</summary>
    Searching,

    /// <summary>An e-invoice is embedded (green).</summary>
    Present,

    /// <summary>No e-invoice (warning colour).</summary>
    Absent,
}

/// <summary>
/// The tab „E-Rechnung“ (KONZEPT.md, section 5.24, „Oberfläche“): „Prüfen“ opens the area of the checker on the right; the group „Info“ says
/// whether the document holds an e-invoice. Searched only when the tab is shown (design decision: most of the time nobody wants this
/// feature, so nothing shows elsewhere), in the background on the file as saved, and remembered per document and state of the file.
/// </summary>
public sealed partial class EInvoiceTabViewModel(MainViewModel main) : ObservableObject, IModuleTabContent
{
    /// <summary>What was found for a document, and for which state of its file.</summary>
    private sealed record Detection(DateTime? SavedAt, InvoiceInfoState State, string Text);

    private readonly ConditionalWeakTable<DocumentViewModel, Detection> _known = new();
    private DocumentViewModel? _document;
    private CancellationTokenSource? _search;

    /// <summary>The text of the group „Info“.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPresent), nameof(IsAbsent))]
    public partial InvoiceInfoState State { get; private set; }

    /// <summary>The text of the group „Info“.</summary>
    [ObservableProperty] public partial string InfoText { get; private set; } = "";

    /// <summary>Green: an e-invoice is embedded.</summary>
    public bool IsPresent => State == InvoiceInfoState.Present;

    /// <summary>Warning colour: none.</summary>
    public bool IsAbsent => State == InvoiceInfoState.Absent;

    /// <summary>Tests: the search of the document shown, to wait for.</summary>
    internal Task Searching { get; private set; } = Task.CompletedTask;

    /// <inheritdoc/>
    public void Shown(DocumentViewModel? document)
    {
        _document = document;
        CheckCommand.NotifyCanExecuteChanged();
        ShowAttachmentsCommand.NotifyCanExecuteChanged();
        SaveXmlCommand.NotifyCanExecuteChanged();
        if (document is null)
        {
            _search?.Cancel();
            (State, InfoText) = (InvoiceInfoState.None, Tr.T("Kein Dokument"));
            return;
        }
        if (_known.TryGetValue(document, out var known) && known.SavedAt == document.SavedAt)
        {
            (State, InfoText) = (known.State, known.Text);
            return;
        }
        Searching = SearchAsync(document);
    }

    /// <summary>Finds the invoice of the file as saved (stage 1 only), in the background.</summary>
    private async Task SearchAsync(DocumentViewModel document)
    {
        _search?.Cancel();
        var cancel = (_search = new CancellationTokenSource()).Token;
        (State, InfoText) = (InvoiceInfoState.Searching, Tr.T("Suche …"));
        var savedAt = document.SavedAt;
        Detection found;
        try
        {
            found = await Task.Run(() =>
            {
                using var copy = document.OpenSavedCopy();
                if (copy is null) return new Detection(savedAt, InvoiceInfoState.Absent, Tr.T("Keine E-Rechnung"));
                var location = InvoiceLocator.Locate(copy, null, cancel);
                return location switch
                {
                    { HasInvoice: false } => new Detection(savedAt, InvoiceInfoState.Absent, Tr.T("Keine E-Rechnung")),
                    { Kind: { } kind } => new Detection(savedAt, InvoiceInfoState.Present, Tr.T("E-Rechnung enthalten ({0})", InvoiceNames.Short(kind))),
                    _ => new Detection(savedAt, InvoiceInfoState.Present, Tr.T("E-Rechnung enthalten, nicht lesbar")),
                };
            }, cancel);
        }
        catch (OperationCanceledException)
        {
            return;   // another document is shown now
        }
        catch (Exception e) when (e is PdfException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            found = new Detection(savedAt, InvoiceInfoState.Absent, Tr.T("Die PDF ließ sich für die Suche nicht öffnen."));
        }
        _known.AddOrUpdate(document, found);
        if (ReferenceEquals(_document, document)) (State, InfoText) = (found.State, found.Text);
    }

    /// <summary>„Prüfen“: opens the area of the checker on the right of the document (again: closes it).</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void Check()
    {
        if (_document is not { } document) return;
        if (document.ModulePanel is EInvoicePanel)
        {
            main.ShowModulePanel(document, null);
            return;
        }
        OpenPanel(document, attachments: false);
    }

    /// <summary>„Anhänge“: the area with its section of attachments open (each can be saved).</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void ShowAttachments()
    {
        if (_document is not { } document) return;
        if (document.ModulePanel is EInvoicePanel { DataContext: EInvoicePanelViewModel open })
        {
            open.AttachmentsOpen = true;
            return;
        }
        OpenPanel(document, attachments: true);
    }

    private void OpenPanel(DocumentViewModel document, bool attachments)
    {
        var model = new EInvoicePanelViewModel(document, () => main.ShowModulePanel(document, null)) { AttachmentsOpen = attachments };
        main.ShowModulePanel(document, new EInvoicePanel { DataContext = model });
    }

    /// <summary>Asks where to save a file (set by the view): the suggested name, the extension without dot; returns the path, or null.</summary>
    public Func<string, string, Task<string?>>? PickSavePath { get; set; }

    /// <summary>Tests: what „XML speichern …“ last reported.</summary>
    internal string? LastMessage { get; private set; }

    /// <summary>
    /// „XML speichern …“: a copy of the embedded invoice XML, byte for byte as embedded, under its own name — written the safe way (a file
    /// beside the target, then moved into place; Fehlergedächtnis 750).
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasInvoice))]
    private async Task SaveXml()
    {
        if (_document is not { } document || PickSavePath is not { } pick) return;
        (byte[]? Data, string Name) found;
        try
        {
            found = await Task.Run(() =>
            {
                using var copy = document.OpenSavedCopy();
                if (copy is null) return ((byte[]?)null, "");
                var location = InvoiceLocator.Locate(copy);
                return (location.Data, location.File?.DisplayName ?? "rechnung.xml");
            });
        }
        catch (Exception e) when (e is PdfException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Report(Tr.T("Die XML ließ sich nicht lesen: {0}", e.Message));
            return;
        }
        if (found.Data is not { } data)
        {
            Report(Tr.T("In dieser PDF ist keine E-Rechnung eingebettet."));
            return;
        }
        // only the name of the embedded file, never a path it may carry
        var name = Path.GetFileName(InvoiceFormat.Plain(found.Name, 120).Replace('\\', '/').Split('/')[^1]);
        if (await pick(name.Length > 0 ? name : "rechnung.xml", "xml") is not { } target) return;
        try
        {
            Core.Pdf.Writing.PdfFileSaver.SaveFile(target, stream => stream.Write(data));
            Report(Tr.T("Die Rechnungs-XML ist gespeichert: {0}", Path.GetFileName(target)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Report(Tr.T("Die XML ließ sich nicht speichern: {0}", e.Message));
        }
    }

    private void Report(string message)
    {
        LastMessage = message;
        main.Message = message;
    }

    private bool HasDocument() => _document is not null;

    private bool HasInvoice() => _document is not null && State == InvoiceInfoState.Present;

    partial void OnStateChanged(InvoiceInfoState value) => SaveXmlCommand.NotifyCanExecuteChanged();
}
