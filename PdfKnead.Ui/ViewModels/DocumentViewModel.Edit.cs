using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.ViewModels;

/// <summary>What the „Bearbeiten“ tool has chosen (KONZEPT.md, section 5.22).</summary>
public enum EditTarget
{
    /// <summary>Nothing yet: the panel on the right says what can be clicked.</summary>
    None,

    /// <summary>A text block is being edited (<see cref="DocumentViewModel.RichEditor"/>); its format panel takes the place of the tool's.</summary>
    Text,

    /// <summary>Images or drawings of the page content are chosen (<see cref="DocumentViewModel.SelectedImages"/>), their details in <see cref="DocumentViewModel.InspectedImage"/>.</summary>
    Image,

    /// <summary>A code was read (<see cref="DocumentViewModel.InspectedBarcode"/>).</summary>
    Code,
}

/// <summary>
/// „Bearbeiten“ (KONZEPT.md, section 5.22): one tool for the text, the images and the codes of the page content. The parts — text
/// editing, image editing with the image details, the code reader — keep their own state; this part says which of them holds the
/// choice, so that choosing one lets go of the others, and gives the hints of the mode.
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>What is chosen in „Bearbeiten“; set by the parts when they choose something, cleared when they let go.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditPanelVisible), nameof(EditHint), nameof(EditPanelTitle))]
    public partial EditTarget EditTarget { get; set; }

    /// <summary>Title of the panel: the tool's name, or what is chosen.</summary>
    public string EditPanelTitle => EditTarget switch
    {
        EditTarget.Image => Tr.T("Bild"),
        EditTarget.Code => Tr.T("Code"),
        _ => Tr.T("Bearbeiten"),
    };

    /// <summary>Whether the details of the chosen image are unfolded in the panel (kept while the document is open).</summary>
    [ObservableProperty] public partial bool ImageDetailsOpen { get; set; }

    /// <summary>One thing is chosen at a time: the other parts let go of theirs. The first choice in a signed document gets the notice.</summary>
    partial void OnEditTargetChanged(EditTarget oldValue, EditTarget newValue)
    {
        if (newValue != EditTarget.Image)
        {
            SelectedImages = null;
            EndImageInfo();
        }
        if (newValue != EditTarget.Code) EndBarcode();
        if (newValue != EditTarget.Text && RichEditor is not null) EndRichEdit();
        if (newValue != EditTarget.None && !_signedEditHintShown && IsSigned())
        {
            _signedEditHintShown = true;
            Notify(Tr.T("Das Dokument ist digital unterschrieben — Bearbeiten macht die Unterschrift ungültig."));
        }
    }

    /// <summary>Whether the tool's own panel is shown on the right: while a text block is edited or an own element is selected, its panel is there instead.</summary>
    public bool IsEditPanelVisible => Tool == ViewTool.Edit && EditTarget != EditTarget.Text && SelectedElement is null;

    /// <summary>Text in the panel while nothing is chosen.</summary>
    public string EditHint => EditTarget == EditTarget.None
        ? Tr.T("Text, ein Bild, einen Code oder ein Formularfeld der Seite anklicken. Alt+Klick wählt, was darunter liegt; ein Bild, das die Seite füllt (Scan), wählt ein Doppelklick.")
        : "";

    private bool _scanHintShown;

    /// <summary>
    /// Whether the objects fill the page (a scan): then a single click passes them by — otherwise every click on a scanned page would
    /// take the page image — and a double-click chooses them. Said once per document when a click passes a scan by.
    /// </summary>
    /// <param name="page">Page index.</param>
    /// <param name="group">Indices of the objects (a picture's tiles).</param>
    /// <returns>Whether they cover at least nine tenths of the page.</returns>
    public bool FillsPage(int page, IReadOnlyList<int> group)
    {
        if ((uint)page >= (uint)Pages.Count || group.Count == 0 || ContentObjectsOn(page) is not { } objects) return false;
        var box = objects.VisibleOf(group[0]);
        foreach (var g in group) box = box.Union(objects.VisibleOf(g));
        var pdfPage = Pages[page].Page;
        return box.Width * box.Height >= 0.9 * pdfPage.Width * pdfPage.Height;
    }

    /// <summary>A click passed a scan by: says once how to choose it.</summary>
    public void NoteScanPassedBy()
    {
        if (_scanHintShown) return;
        _scanHintShown = true;
        Notify(Tr.T("Ein Bild, das die Seite füllt (Scan), wählt ein Doppelklick — so trifft ein Klick auf der Seite nicht jedes Mal das Seitenbild."));
    }

    /// <summary>Whether the tool is on; the mode's state is let go of when it is switched off.</summary>
    private void EditToolChanged(ViewTool oldValue, ViewTool newValue)
    {
        OnPropertyChanged(nameof(IsEditPanelVisible));
        // the details of an image shown while reading belong to that tool
        if (oldValue != newValue && InspectedImage is not null) EndImageInfo();
        OnPropertyChanged(nameof(IsReadingImageInfo));
        if (newValue == ViewTool.Edit)
        {
            // Read the font folders in the background as soon as the mode is switched on, not only at the first edit
            RichTextPropertiesViewModel.LoadFamilies();
            return;
        }
        if (oldValue != ViewTool.Edit) return;
        EditTarget = EditTarget.None;
        EndRichEdit();
        ResetBlocks();
        IsCroppingImages = false;
        ResetContentObjects();
    }
}
