using Avalonia.Controls;
using PdfKnead.ViewModels;

namespace PdfKnead.Modules;

/// <summary>
/// A tab a module of its own adds to the ribbon, after „Sicher“ (KONZEPT.md, section 5.24, „Aufbau und Entfernen“): the main window knows no
/// module by name — a module registers its tab here once at start, from the host program, and removing the module removes the tab. The user
/// can hide it in the settings (<see cref="Core.Platform.AppSettings.HiddenModules"/>).
/// </summary>
public sealed class ModuleTab
{
    /// <summary>A stable key, also what the settings keep when the tab is hidden.</summary>
    public required string Key { get; init; }

    /// <summary>The text of the tab in the current language.</summary>
    public required Func<string> Header { get; init; }

    /// <summary>
    /// Creates the groups of the tab when it is first shown: the module's view with its own data context. A data context that implements
    /// <see cref="IModuleTabContent"/> learns when the tab is shown and for which document.
    /// </summary>
    public required Func<MainViewModel, Control> CreateContent { get; init; }

    /// <summary>
    /// Optional: called once per opened document when it first differs from its saved state, while the tab is not hidden — for a hint of the
    /// module (through <see cref="MainViewModel.Message"/>). It must not change the document, and it works on the file as saved.
    /// </summary>
    public Action<MainViewModel, DocumentViewModel>? FirstChange { get; init; }

    /// <summary>
    /// Optional: advice for „Als PDF/A speichern …“ — a sentence shown at the top of the dialog and the part to choose and mark as fitting
    /// (for instance one that keeps the attachments the module looks after); null: none. Asked only for tabs shown, on the file as saved;
    /// it must not change the document.
    /// </summary>
    public Func<DocumentViewModel, Task<PdfAAdvice?>>? PdfAAdvice { get; init; }

    /// <summary>
    /// Optional: an entry for „Jetzt schwärzen?“ — a place outside the page text the module knows of and the bars cannot reach (for instance
    /// structured data that write the values otherwise), with the item of „Dokument bereinigen“ that removes it and what stays if it is
    /// kept; null: none. Asked only for tabs shown; it must not change the document.
    /// </summary>
    public Func<DocumentViewModel, Task<RedactionElsewhere?>>? RedactionElsewhere { get; init; }

    /// <summary>
    /// Optional: a row of its own for „Dokument bereinigen“ — attachments the module looks after (for instance an e-invoice), shown apart from
    /// „Eingebettete Dateien“ and not ticked at first; null: none. Called in the background on the document as it is in memory, read only
    /// (like the inspection of the area).
    /// </summary>
    public Func<Core.Pdf.Document.PdfDocument, ModuleCleanupFinding?>? CleanupFinding { get; init; }
}

/// <summary>A row of a module in „Dokument bereinigen“ (not ticked at first).</summary>
/// <param name="Title">What, e.g. „E-Rechnung (factur-x.xml)“.</param>
/// <param name="Note">Said under it: what follows if it is removed.</param>
/// <param name="Files">The data streams of its embedded files in the document: they stay unless the row is ticked.</param>
/// <param name="Attachments">How many of the attachments „Eingebettete Dateien“ counts are these (that row counts the others).</param>
public sealed record ModuleCleanupFinding(string Title, string Note, IReadOnlySet<Core.Pdf.Objects.PdfObjectId> Files, int Attachments);

/// <summary>What a module advises for „Als PDF/A speichern …“: the sentence at the top of the dialog and the part chosen first.</summary>
/// <param name="Note">One or two sentences.</param>
/// <param name="Preferred">The part chosen and marked as fitting, if the file can meet it.</param>
public sealed record PdfAAdvice(string Note, Core.Pdf.Writing.PdfAConformance Preferred);

/// <summary>The data context of a module tab that wants to know when it is shown, and for which document (its work starts only then).</summary>
public interface IModuleTabContent
{
    /// <summary>The tab is shown, or the document changed while it is shown.</summary>
    /// <param name="document">The selected document; null without one.</param>
    void Shown(DocumentViewModel? document);
}

/// <summary>The modules registered by the host program (before the main window is created).</summary>
public static class UiModules
{
    /// <summary>The tabs of the modules, in the order registered.</summary>
    public static List<ModuleTab> Tabs { get; } = [];
}
