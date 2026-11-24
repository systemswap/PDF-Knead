using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Ai;
using PdfKnead.Core.Forms.Recognition;
using PdfKnead.Core.Ocr;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>„Formular“ tab: design forms, insert and arrange fields, properties on the right.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Properties of the selected fields (panel on the right).</summary>
    public FieldPropertiesViewModel FieldProperties { get; } = new();

    /// <summary>Properties of the selected shape (panel on the right). See KONZEPT.md, section 5.13.</summary>
    public ElementPropertiesViewModel ElementProperties { get; } = new();

    /// <summary>Properties of the selected code (KONZEPT.md, section 5.19).</summary>
    public CodePropertiesViewModel CodeProperties { get; } = new();

    /// <summary>Formatting of edited text (panel on the right). See KONZEPT.md, section 5.14.</summary>
    public RichTextPropertiesViewModel RichTextProperties { get; } = new();

    /// <summary>Whether „Formular bearbeiten“ mode is on (also while a field is being inserted).</summary>
    public bool IsFormDesign => Tool is ViewTool.DesignForm or ViewTool.InsertFormField;

    /// <summary>The field type currently being inserted (for the buttons); null when the tool is not active.</summary>
    public FieldPreset? ActivePreset => Tool == ViewTool.InsertFormField ? _fieldPreset : null;

    private FieldPreset _fieldPreset;

    /// <summary>Whether several fields are selected (for arranging).</summary>
    public bool HasManyFields => SelectedDocument?.FieldSelection.Count > 1;

    public bool HasFieldsSelected => SelectedDocument is { IsFieldDesign: true, FieldSelection.Count: > 0 };

    private void OnFieldSelectionChanged() => RefreshFormDesign();

    partial void OnSidebarTabChanged(SidebarTab value)
    {
        if (value == SidebarTab.Fields) RefreshFieldList();
    }

    /// <summary>All fields of the document by page (the „Felder“ tab of the sidebar).</summary>
    public IReadOnlyList<FieldListItem> FieldList { get; private set; } = [];

    private void RefreshFieldList()
    {
        var list = new List<FieldListItem>();
        if (SelectedDocument is { } doc)
            for (var page = 0; page < doc.History.Count; page++)
                foreach (var element in doc.History.Pages[page].Elements)
                    if (DocumentViewModel.IsFormField(element))
                        list.Add(new FieldListItem(page, element, doc.FieldNameOf(element), FieldListItem.KindOf(doc.SpecOf(element))));
        FieldList = list;
        OnPropertyChanged(nameof(FieldList));
        OnPropertyChanged(nameof(HasNoFields));
    }

    public bool HasNoFields => FieldList.Count == 0;

    /// <summary>A field chosen in the list: switches to „Formular bearbeiten“ mode, selects it and scrolls to it. Always reads as null.</summary>
    public FieldListItem? SelectedFieldItem
    {
        get => null;
        set
        {
            if (value is null || SelectedDocument is not { } doc) return;
            if (!IsFormDesign && !EnsureFormsAllowed()) return;
            if (!IsFormDesign) Tool = ViewTool.DesignForm;
            doc.SelectFields([new ElementRef(value.Page, value.Element)]);
            doc.ScrollToPage(value.Page);
        }
    }

    private void RefreshFormDesign()
    {
        if (SidebarTab == SidebarTab.Fields) RefreshFieldList();
        FieldProperties.Refresh(SelectedDocument);
        ElementProperties.Refresh(SelectedDocument);
        CodeProperties.Refresh(SelectedDocument);
        RichTextProperties.Refresh(SelectedDocument);
        MoveLayerCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowTextProperties));
        OnPropertyChanged(nameof(ShowLayers));
        OnPropertyChanged(nameof(IsFormDesign));
        OnPropertyChanged(nameof(IsEditTool));
        OnPropertyChanged(nameof(ModeBannerVisible));
        OnPropertyChanged(nameof(ActivePreset));
        OnPropertyChanged(nameof(HasManyFields));
        OnPropertyChanged(nameof(HasFieldsSelected));
        OnPropertyChanged(nameof(ShowTabOrder));
        if (IsFormDesign) SelectedDocument?.HintFormDesign();
        else if (SelectedDocument is { ShowTabOrder: true } doc) doc.SetShowTabOrder(false);
        ArrangeFieldsCommand.NotifyCanExecuteChanged();
        DistributeFieldsCommand.NotifyCanExecuteChanged();
        DuplicateFieldsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Bar at the top while fields or the page content can be changed, so that it is obvious at once that a mode is on (the same for
    /// „Formular bearbeiten“ and „Bearbeiten“; see KONZEPT.md, sections 5.4a and 5.22).
    /// </summary>
    public bool ModeBannerVisible => IsFormDesign || IsEditTool;

    /// <summary>Ends the mode shown in the bar: switches „Formular bearbeiten“ or „Bearbeiten“ off.</summary>
    [RelayCommand]
    private void EndMode()
    {
        if (IsFormDesign) ToggleFormDesign();
        else if (IsEditTool) Tool = ViewTool.SelectText;
    }

    /// <summary>Switches „Bearbeiten“ on and off („Start“ tab, Ctrl+E). See KONZEPT.md, section 5.22.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToggleEdit()
    {
        if (!IsEditTool && !EnsureAllowed("start.edit")) return;
        SelectedDocument?.SelectFields([]);
        Tool = IsEditTool ? ViewTool.SelectText : ViewTool.Edit;
    }

    /// <summary>
    /// Creating forms can be switched off by the function file of the organization: checked when „Formular bearbeiten“, a field tool, the tab
    /// order or „Felder erkennen“ starts. Filling in forms stays possible.
    /// </summary>
    private bool EnsureFormsAllowed() => EnsureAllowed("form.all");

    /// <summary>Switches „Formular bearbeiten“ on and off.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToggleFormDesign()
    {
        if (!IsFormDesign && !EnsureFormsAllowed()) return;
        SelectedDocument?.SelectFields([]);
        Tool = IsFormDesign ? ViewTool.SelectText : ViewTool.DesignForm;
    }

    /// <summary>Insert field: chooses the type (stays active for further fields); the same button again returns to „Formular bearbeiten“.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void InsertFormField(FieldPreset preset)
    {
        if (Tool == ViewTool.InsertFormField && _fieldPreset == preset)
        {
            Tool = ViewTool.DesignForm;
            return;
        }
        // a signature field belongs to signing, which stays free; every other field creates a form
        if (preset != FieldPreset.Signature && !EnsureFormsAllowed()) return;
        _fieldPreset = preset;
        foreach (var doc in Documents)
        {
            doc.FieldPreset = preset;
            doc.StartFieldTool();
        }
        if (Tool == ViewTool.InsertFormField) RefreshFormDesign();
        else Tool = ViewTool.InsertFormField;
    }

    /// <summary>Whether the tab order is shown and can be set by clicking (in „Formular bearbeiten“ mode).</summary>
    public bool ShowTabOrder => SelectedDocument?.ShowTabOrder == true;

    private static string TabOrderHint => Tr.T("Felder nacheinander anklicken: das erste wird Nummer 1, das nächste Nummer 2 … Esc beendet.");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToggleTabOrder()
    {
        if (SelectedDocument is not { } doc) return;
        if (!doc.ShowTabOrder && !EnsureFormsAllowed()) return;
        if (!IsFormDesign) Tool = ViewTool.DesignForm;
        doc.SetShowTabOrder(!doc.ShowTabOrder);
        if (doc.ShowTabOrder) doc.Notify(TabOrderHint);
        OnPropertyChanged(nameof(ShowTabOrder));
    }

    /// <summary>Fill in and test the form: switches „Formular bearbeiten“ off.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void TestForm()
    {
        SelectedDocument?.SelectFields([]);
        Tool = ViewTool.SelectText;
    }

    private bool CanArrange => SelectedDocument?.FieldSelection.Count > 1;

    [RelayCommand(CanExecute = nameof(CanArrange))]
    private void ArrangeFields(FieldArrange arrange) => SelectedDocument?.ArrangeFields(arrange);

    /// <summary>Gap for distributing: „Gleichmäßig“ or a value in points (typed or chosen).</summary>
    [ObservableProperty] public partial string DistributeGap { get; set; } = Tr.T("Gleichmäßig");

    public IReadOnlyList<string> GapOptions => [Tr.T("Gleichmäßig"), "2", "4", "6", "8", "10", "12", "16", "20", "24", "32"];

    /// <summary>Distribute: evenly between the first and last field, or with the chosen gap (points, clamped to 0 … 500).</summary>
    [RelayCommand(CanExecute = nameof(CanArrange))]
    private void DistributeFields(FieldArrange arrange)
    {
        var text = (DistributeGap ?? "").Trim().Replace(',', '.').Replace("pt", "").Trim();
        double? gap = double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g) && double.IsFinite(g)
            ? Math.Clamp(g, 0, 500)
            : null;
        // Distributing two fields evenly changes nothing (first and last stay), so say so instead of silently doing nothing
        if (gap is null && SelectedDocument?.FieldSelection.Count < 3)
        {
            Message = Tr.T("Gleichmäßig verteilen braucht mindestens drei Felder — für zwei einen Abstand wählen.");
            return;
        }
        SelectedDocument?.ArrangeFields(arrange, gap);
    }

    private bool CanDuplicate => SelectedDocument is { IsFieldDesign: true, FieldSelection.Count: > 0 };

    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private void DuplicateFields()
    {
        if (EnsureFormsAllowed()) SelectedDocument?.DuplicateSelectedFields();
    }
    /// <summary>
    /// Reiter „Formular“ → „Felder erkennen“ (KONZEPT.md, section 5.9a): proposals for fields at lines, boxes and tables of the pages, in the
    /// area on the right; a second click closes it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RecognizeFields()
    {
        if (SelectedDocument is not { } doc || !CheckRight(doc.CanDesignForms)) return;
        if (doc.IsFieldProposalsOpen)
        {
            doc.CloseFieldProposals();
            return;
        }
        if (!EnsureFormsAllowed()) return;
        if (doc.IsCleanupOpen) doc.CloseCleanup();
        if (doc.IsNotesOpen) doc.CloseNotes();
        if (doc.IsAccessibilityOpen) doc.CloseAccessibility();
        // one area at a time on the right
        if (Tool is not ViewTool.SelectText and not ViewTool.Pan) Tool = ViewTool.SelectText;
        try
        {
            // with the models that are there: the text recognition of the size chosen last (or another one installed), the field detector
            var (canRead, canDetect) = FieldModelsThere();
            var installed = OcrModels.Installed;
            var size = installed.Count == 0 || installed.Contains(UserSettings.Current.OcrSize) ? UserSettings.Current.OcrSize : installed[^1];
            Func<ITextRecognizer?>? read = canRead ? () => OpenRecognizer(size) : null;
            Func<IFieldDetector?>? detect = canDetect ? () => OpenFieldDetector() : null;
            await doc.RecognizeFieldsAsync(read, detect);
        }
        catch (OperationCanceledException)
        {
            // the tab has been closed in the meantime
        }
    }

    /// <summary>Which models „Felder erkennen“ can use: a text recognition, the field detector (set by the tests).</summary>
    internal Func<(bool Read, bool Detect)> FieldModelsThere { get; set; } = () => (OcrModels.Installed.Count > 0, OcrModels.FieldDetectorPath is not null);

    /// <summary>Opens the field detector (set by the tests: one that needs no model); by default FFDetr from the folder „ocr“.</summary>
    internal Func<IFieldDetector> OpenFieldDetector { get; set; } = () => FfdetrDetector.Open();

    /// <summary>„Übernehmen“ in the area: the ticked proposals become fields (one undo step), then „Formular bearbeiten“.</summary>
    [RelayCommand]
    private async Task AcceptFieldProposals()
    {
        if (SelectedDocument is not { IsFieldProposalsOpen: true } doc) return;
        // without a word the click would seem to have been lost
        if (!doc.FieldProposals.Any(r => r.IsChecked))
        {
            ShowNotice(Tr.T("Kein Vorschlag angehakt — nichts übernommen."));
            return;
        }
        var count = await doc.AcceptFieldProposalsAsync();
        if (count == 0) return;
        Tool = ViewTool.DesignForm;
        ShowNotice(count == 1 ? Tr.T("1 Feld angelegt — weiter in „Formular bearbeiten“.") : Tr.T("{0} Felder angelegt — weiter in „Formular bearbeiten“.", count));
    }

    /// <summary>Closes the area of the proposals without taking any over.</summary>
    [RelayCommand]
    private void CloseFieldProposals() => SelectedDocument?.CloseFieldProposals();

    /// <summary>Ticks all proposals („true“) or none.</summary>
    [RelayCommand]
    private void TickFieldProposals(string all) => SelectedDocument?.TickFieldProposals(all == "true");
}

/// <summary>A field in the field list: page, name, type.</summary>
public sealed record FieldListItem(int Page, Core.Pdf.Editing.PageElement Element, string Name, string Kind)
{
    public string PageText => Tr.T("Seite {0}", Page + 1);

    public static string KindOf(Core.Pdf.Editing.FieldSpec? spec) => spec?.Code is { } code ? CodeHints.FieldTitle(code) : spec?.Kind switch
    {
        Core.Pdf.Editing.FormFieldKind.CheckBox => Tr.T("Kontrollkästchen"),
        Core.Pdf.Editing.FormFieldKind.Radio => Tr.T("Optionsfeld"),
        Core.Pdf.Editing.FormFieldKind.ComboBox => Tr.T("Kombinationsfeld"),
        Core.Pdf.Editing.FormFieldKind.ListBox => Tr.T("Listenfeld"),
        Core.Pdf.Editing.FormFieldKind.PushButton => Tr.T("Schaltfläche"),
        Core.Pdf.Editing.FormFieldKind.Signature => Tr.T("Unterschriftsfeld"),
        _ => spec?.Format.Kind switch
        {
            Core.Pdf.Editing.FieldFormatKind.Date => Tr.T("Datumsfeld"),
            Core.Pdf.Editing.FieldFormatKind.Number or Core.Pdf.Editing.FieldFormatKind.Percent => Tr.T("Zahlenfeld"),
            _ => Tr.T("Textfeld"),
        },
    };

}
