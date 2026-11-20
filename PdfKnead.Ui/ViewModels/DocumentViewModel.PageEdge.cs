using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Localization;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>
/// Elements at the edge of the page (decided on 8.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 622): the view shows them cut at the visible
/// area, as readers do. An image placed or changed so that it reaches beyond it asks whether to cut that part off — invisible, it would stay
/// in the file, where anyone could take it out („Behalten“ for bleed in print; „Nicht mehr fragen“, on again in the area of the image); a code
/// reaching beyond it gets a note (it cannot be read whole).
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>A question about the edge is open (another placement does not ask again meanwhile).</summary>
    private bool _askingEdge;

    /// <summary>Whether to ask about cutting off what an image has beyond the edge of the page (saved with the settings).</summary>
    public bool AskCropAtPageEdge
    {
        get => UserSettings.Current.AskCropOverhang;
        set
        {
            if (value == AskCropAtPageEdge) return;
            UserSettings.Update(s => s with { AskCropOverhang = value });
            OnPropertyChanged();
        }
    }

    /// <summary>Whether an element reaches beyond the visible area of its page.</summary>
    public bool OverhangsPage(ElementRef element) =>
        (uint)element.Page < (uint)History.Count && PageEdge.Overhangs(element.Element, History.Pages[element.Page].Page.CropBox);

    /// <summary>
    /// After an element was placed or changed (not while it is nudged with the arrow keys): an image beyond the edge asks, a code gets a note.
    /// </summary>
    private void CheckPageEdge(ElementRef placed)
    {
        if (!OverhangsPage(placed)) return;
        switch (placed.Element)
        {
            case CodeElement:
                Notify(Tr.T("Der Code ragt über den Blattrand — so lässt er sich nicht vollständig lesen."));
                break;
            case ImageElement image when AskCropAtPageEdge && AskChoices is { } ask && !_askingEdge &&
                                         PageEdge.CropToPage(image, History.Pages[placed.Page].Page.CropBox) is not null:
                _ = AskCropAsync(placed, ask);
                break;
        }
    }

    /// <summary>„Bild ragt über den Blattrand“: „Abschneiden“ (the default) cuts it off, „Behalten“ leaves it; „Nicht mehr fragen“ is saved.</summary>
    private async Task AskCropAsync(ElementRef placed, Func<ChoiceRequest, Task<ChoiceAnswer?>> ask)
    {
        _askingEdge = true;
        ChoiceAnswer? answer;
        try
        {
            answer = await ask(new ChoiceRequest(Tr.T("Bild ragt über den Blattrand"),
                Tr.T("Der Teil außerhalb des Blatts ist unsichtbar und wird nicht gedruckt, bliebe aber in der Datei — jeder könnte ihn herauslösen. Soll er abgeschnitten werden?"),
                [Tr.T("Abschneiden")], Tr.T("Behalten"), Tr.T("Nicht mehr fragen (etwa bei Anschnitt für den Druck; wieder einschaltbar beim Bild)"), "Icon.Image"));
        }
        finally
        {
            _askingEdge = false;
        }
        if (answer is not { } given) return;
        if (given.Checked) AskCropAtPageEdge = false;
        if (given.Answer == 1) CropAtPageEdge(placed);
    }

    /// <summary>
    /// Cuts off what an image has beyond the edge of its page (<see cref="PageEdge.CropToPage"/>; one undo step); it stays selected. The
    /// element must still be on its page as it was (a change in between: nothing happens).
    /// </summary>
    /// <returns>Whether something was cut off.</returns>
    public bool CropAtPageEdge(ElementRef placed)
    {
        if (placed.Element is not ImageElement image || (uint)placed.Page >= (uint)History.Count ||
            !History.Pages[placed.Page].Elements.Any(e => ReferenceEquals(e, image)) ||
            PageEdge.CropToPage(image, History.Pages[placed.Page].Page.CropBox) is not { } cut) return false;
        return ReplaceElement(placed, cut, Tr.T("Am Blattrand abschneiden"));
    }
}
