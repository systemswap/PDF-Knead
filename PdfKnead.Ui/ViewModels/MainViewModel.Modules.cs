using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Modules;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>A tab of a module in the ribbon: its text, its groups (created when first shown) and whether it is the selected one.</summary>
public sealed partial class ModuleTabViewModel : ObservableObject
{
    private readonly ModuleTab _tab;
    private readonly MainViewModel _main;
    private Control? _content;

    internal ModuleTabViewModel(ModuleTab tab, MainViewModel main)
    {
        (_tab, _main) = (tab, main);
        Tr.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Header));
    }

    /// <summary>The key of the module.</summary>
    public string Key => _tab.Key;

    /// <summary>The text of the tab in the current language.</summary>
    public string Header => _tab.Header();

    /// <summary>The groups of the tab, created when first asked for.</summary>
    public Control Content => _content ??= _tab.CreateContent(_main);

    /// <summary>Whether this tab is the one shown.</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>Tells the content which document it is shown for (if it wants to know).</summary>
    internal void Shown(DocumentViewModel? document) => (Content.DataContext as IModuleTabContent)?.Shown(document);

    /// <summary>The module's row for „Dokument bereinigen“, if it gives any.</summary>
    internal Func<Core.Pdf.Document.PdfDocument, ModuleCleanupFinding?>? CleanupFinding => _tab.CleanupFinding;

    /// <summary>The module's entry for „Jetzt schwärzen?“, if it gives any.</summary>
    internal Func<DocumentViewModel, Task<RedactionElsewhere?>>? RedactionElsewhere => _tab.RedactionElsewhere;

    /// <summary>The module's advice for „Als PDF/A speichern …“, if it gives any.</summary>
    internal Func<DocumentViewModel, Task<PdfAAdvice?>>? PdfAAdvice => _tab.PdfAAdvice;

    /// <summary>Tells the module that a document was changed for the first time (if it wants to know).</summary>
    internal void FirstChange(DocumentViewModel document) => _tab.FirstChange?.Invoke(_main, document);
}

/// <summary>The tabs and areas of modules of their own (KONZEPT.md, section 5.24, „Aufbau und Entfernen“; <see cref="UiModules"/>).</summary>
public sealed partial class MainViewModel
{
    /// <summary>The tabs of the registered modules the user has not hidden, after „Sicher“.</summary>
    public ObservableCollection<ModuleTabViewModel> ModuleTabs { get; } = [];

    /// <summary>The module tab shown when <see cref="RibbonTab"/> is <see cref="RibbonTab.Module"/>.</summary>
    [ObservableProperty] public partial ModuleTabViewModel? SelectedModuleTab { get; set; }

    /// <summary>Builds the module tabs anew from the registered modules and the settings (at start and when the settings change).</summary>
    internal void LoadModuleTabs()
    {
        var hidden = UserSettings.Current.HiddenModules ?? [];
        var keep = SelectedModuleTab?.Key;
        ModuleTabs.Clear();
        foreach (var tab in UiModules.Tabs)
            if (!hidden.Contains(tab.Key)) ModuleTabs.Add(new ModuleTabViewModel(tab, this));
        SelectedModuleTab = ModuleTabs.FirstOrDefault(t => t.Key == keep);
        // the tab shown was hidden: back to „Start“; its areas close with it
        if (SelectedModuleTab is null && RibbonTab == RibbonTab.Module) RibbonTab = RibbonTab.Start;
        if (SelectedModuleTab is null)
            foreach (var doc in Documents) doc.ModulePanel = null;
        MarkModuleTab();
    }

    [RelayCommand]
    private void SelectModuleTab(ModuleTabViewModel tab)
    {
        if (RibbonCollapsed) RibbonPeek = !(RibbonPeek && RibbonTab == RibbonTab.Module && SelectedModuleTab == tab);
        SelectedModuleTab = tab;
        RibbonTab = RibbonTab.Module;
        MarkModuleTab();
        tab.Shown(SelectedDocument);
    }

    partial void OnRibbonTabChanged(RibbonTab value) => MarkModuleTab();

    /// <summary>Which module tab is selected (for the look of the tab row).</summary>
    private void MarkModuleTab()
    {
        foreach (var tab in ModuleTabs) tab.IsSelected = RibbonTab == RibbonTab.Module && ReferenceEquals(tab, SelectedModuleTab);
    }

    /// <summary>The document changed: a module tab that is shown learns it.</summary>
    private void ShowModuleTabFor(DocumentViewModel? document)
    {
        if (RibbonTab == RibbonTab.Module && SelectedModuleTab is { } tab) tab.Shown(document);
    }

    /// <summary>Watches each opened document for its first change; a closed one no longer.</summary>
    private void WatchFirstChanges(NotifyCollectionChangedEventArgs e)
    {
        foreach (var doc in e.OldItems?.OfType<DocumentViewModel>() ?? []) doc.PropertyChanged -= OnDocumentPropertyChanged;
        foreach (var doc in e.NewItems?.OfType<DocumentViewModel>() ?? []) doc.PropertyChanged += OnDocumentPropertyChanged;
    }

    /// <summary>The first change of a document: the module tabs shown learn it, once per document.</summary>
    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DocumentViewModel.IsModified) || sender is not DocumentViewModel { IsModified: true } doc) return;
        doc.PropertyChanged -= OnDocumentPropertyChanged;
        foreach (var tab in ModuleTabs) tab.FirstChange(doc);
    }

    /// <summary>The rows the module tabs shown give for „Dokument bereinigen“.</summary>
    internal IReadOnlyList<Func<Core.Pdf.Document.PdfDocument, ModuleCleanupFinding?>> ModuleCleanupFinders() =>
        [.. ModuleTabs.Select(t => t.CleanupFinding).OfType<Func<Core.Pdf.Document.PdfDocument, ModuleCleanupFinding?>>()];

    /// <summary>The entries of the module tabs shown for „Jetzt schwärzen?“, each asked for at most 10 s.</summary>
    private async Task<List<RedactionElsewhere>> ModuleRedactionElsewhereAsync(DocumentViewModel document)
    {
        var found = new List<RedactionElsewhere>();
        foreach (var tab in ModuleTabs)
        {
            if (tab.RedactionElsewhere is not { } ask) continue;
            var asked = ask(document);
            if (await Task.WhenAny(asked, Task.Delay(TimeSpan.FromSeconds(10))) == asked && asked.IsCompletedSuccessfully && asked.Result is { } entry)
                found.Add(entry);
        }
        return found;
    }

    /// <summary>The advice of the module tabs shown for „Als PDF/A speichern …“ (the first one given), at most 10 s; null: none.</summary>
    private async Task<PdfAAdvice?> ModulePdfAAdviceAsync(DocumentViewModel document)
    {
        foreach (var tab in ModuleTabs)
        {
            if (tab.PdfAAdvice is not { } ask) continue;
            var asked = ask(document);
            if (await Task.WhenAny(asked, Task.Delay(TimeSpan.FromSeconds(10))) == asked && asked.IsCompletedSuccessfully && asked.Result is { } advice)
                return advice;
        }
        return null;
    }

    /// <summary>
    /// Opens the area of a module on the right of a document (one area at a time on the right: the others close, as for „Barrierefreiheit“);
    /// null closes it.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="panel">The area; null closes the area of the module.</param>
    public void ShowModulePanel(DocumentViewModel document, Control? panel)
    {
        if (panel is not null)
        {
            if (Tool is not ViewTool.SelectText and not ViewTool.Pan) Tool = ViewTool.SelectText;
            if (document.IsAccessibilityOpen) document.CloseAccessibility();
            if (document.IsCleanupOpen) document.CloseCleanup();
            if (document.IsNotesOpen) document.CloseNotes();
            if (document.IsFieldProposalsOpen) document.CloseFieldProposals();
        }
        document.ModulePanel = panel;
    }
}
