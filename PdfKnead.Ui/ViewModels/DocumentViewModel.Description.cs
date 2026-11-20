using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.ViewModels;

/// <summary>A pending change of the description for screen readers: title, language and whether readers show the title (saved with the file).</summary>
public sealed record DescriptionChange(string Title, string Language, bool DisplayTitle);

/// <summary>
/// Title, language and „Titel statt Dateiname anzeigen“ in „Eigenschaften“ (docs/ABLAUFPLAN.md, 3.3 a; ISO 14289-1, 7.1 and 7.2;
/// docs/FEHLERGEDAECHTNIS.md, No. 492): edited there, applied as a change of the document, written when saving.
/// </summary>
public partial class DocumentViewModel
{
    private static readonly PdfName LangKey = PdfName.Get("Lang"), ViewerPreferencesKey = PdfName.Get("ViewerPreferences");

    /// <summary>
    /// „Titel behalten“ when cleaning up the metadata: preset for a tagged file only, where readers for the blind announce the title (PDF/UA);
    /// in an untagged one cleaning up removes it too — titles often name a person (decided on 5.11.2026; before: always preset).
    /// </summary>
    [ObservableProperty]
    public partial bool KeepTitleOnCleanup { get; set; }

    /// <summary>The file has a structure tree (it is tagged).</summary>
    public bool IsTagged => Document.Catalog?.GetDictionary(PdfName.Get("StructTreeRoot")) is not null;

    /// <summary>The description to write on the next save; null: as in the file. Counts as a modification.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified), nameof(Title))]
    public partial DescriptionChange? DescriptionOnSave { get; set; }

    /// <summary>The title as edited in „Eigenschaften“.</summary>
    [ObservableProperty]
    public partial string EditTitle { get; set; } = "";

    /// <summary>The language as edited (a language tag such as „de-DE“).</summary>
    [ObservableProperty]
    public partial string EditLanguage { get; set; } = "";

    /// <summary>Readers show the title instead of the file name.</summary>
    [ObservableProperty]
    public partial bool EditDisplayTitle { get; set; }

    /// <summary>The description as the file has it.</summary>
    private DescriptionChange FileDescription() => new(
        Document.Info.Title ?? "",
        Document.Catalog?.GetString(LangKey)?.ToText() ?? "",
        Document.Catalog?.GetDictionary(ViewerPreferencesKey)?.GetBoolean(PdfName.Get("DisplayDocTitle")) == true);

    /// <summary>The edit fields from the pending change or the file.</summary>
    public void ResetDescription()
    {
        var shown = DescriptionOnSave ?? FileDescription();
        EditTitle = shown.Title;
        EditLanguage = shown.Language;
        EditDisplayTitle = shown.DisplayTitle;
    }

    /// <summary>Takes the edited description as a change of the document (none if it equals the file's).</summary>
    [RelayCommand]
    private void ApplyDescription()
    {
        var change = new DescriptionChange(EditTitle.Trim(), EditLanguage.Trim(), EditDisplayTitle);
        DescriptionOnSave = change == FileDescription() ? null : change;
        OnPropertyChanged(nameof(Properties));
    }

    /// <summary>Proposes the language the text of the pages appears to be in.</summary>
    [RelayCommand]
    private async Task ProposeLanguage()
    {
        var list = History.Pages.Select(p => (p.Source, p.Page)).ToList();
        var proposed = await Task.Run(() => PdfATagging.ProposeLanguage(list));
        if (proposed.Length > 0) EditLanguage = proposed;
    }
}
