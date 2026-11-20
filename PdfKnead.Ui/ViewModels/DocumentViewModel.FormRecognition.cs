using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Forms.Recognition;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.ViewModels;

/// <summary>A proposed field in the list of „Felder erkennen“: take it over or not, its name (changeable), kind, label and page.</summary>
public sealed partial class FieldProposalRow : ObservableObject
{
    /// <param name="proposal">The proposal.</param>
    public FieldProposalRow(FieldProposal proposal)
    {
        Proposal = proposal;
        Name = proposal.Spec.Name;
        // the less certain ones (without a label) are shown, but not ticked
        IsChecked = proposal.Confidence >= 0.6;
    }

    /// <summary>The proposal.</summary>
    public FieldProposal Proposal { get; }

    /// <summary>Taken over with „Übernehmen“.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>The name the field gets (made unique when taken over).</summary>
    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>The kind as the list shows it.</summary>
    public string Kind => Proposal.Spec switch
    {
        { Kind: FormFieldKind.Signature } => Tr.T("Unterschriftsfeld"),
        { Kind: FormFieldKind.CheckBox } => Tr.T("Kontrollkästchen"),
        { Kind: FormFieldKind.Radio } spec => Tr.T("Option „{0}“", spec.ExportValue),
        { Comb: true } spec => Tr.T("Kammfeld, {0} Zeichen", spec.MaxLength),
        { Format.Kind: FieldFormatKind.Date } => Tr.T("Datum"),
        { Format.Kind: FieldFormatKind.Number, Format.Decimals: > 0 } => Tr.T("Betrag"),
        { Format.Kind: FieldFormatKind.Number } => Tr.T("Zahl"),
        { Multiline: true } => Tr.T("Textfeld, mehrzeilig"),
        _ => Tr.T("Textfeld"),
    };

    /// <summary>Linked to proposals on other pages (the same label at the same place): one field, one value.</summary>
    [ObservableProperty]
    public partial bool IsLinked { get; set; }

    /// <summary>Only the field detector saw it.</summary>
    public bool IsModel => Proposal.Source == ProposalSource.Model;

    /// <summary>„Seite 3“.</summary>
    public string PageText => Tr.T("Seite {0}", Proposal.Page + 1);

    /// <summary>The label the field was named after, or a note that none was found.</summary>
    public string Label => Proposal.Label.Length > 0 ? Proposal.Label : Tr.T("ohne Beschriftung");
}

/// <summary>
/// „Felder erkennen“ (KONZEPT.md, section 5.9a, the part without AI): the recognition runs page by page in the background; its proposals are
/// shown dashed on the pages and listed on the right, and the ticked ones are taken over as one undo step — then „Formular bearbeiten“.
/// </summary>
public sealed partial class DocumentViewModel
{
    private CancellationTokenSource? _fieldRecognition;

    /// <summary>The proposals of the last run, in reading order.</summary>
    public ObservableCollection<FieldProposalRow> FieldProposals { get; } = [];

    /// <summary>The area with the proposals is open.</summary>
    [ObservableProperty]
    public partial bool IsFieldProposalsOpen { get; set; }

    /// <summary>The recognition is running.</summary>
    [ObservableProperty]
    public partial bool FieldRecognitionRunning { get; set; }

    /// <summary>What the area says about the run („Seite 3 von 12 …“, „12 Felder vorgeschlagen“).</summary>
    [ObservableProperty]
    public partial string FieldRecognitionStatus { get; set; } = "";

    /// <summary>The proposals or their ticks changed: the pages draw them anew.</summary>
    public event Action? FieldProposalsChanged;

    /// <summary>What the area says about the models: what they added, or what would help on these pages; empty if nothing.</summary>
    [ObservableProperty]
    public partial string FieldRecognitionHint { get; set; } = "";

    // the text read in scans during the last run, per page — put over the pages with „Übernehmen“ if wanted
    private Dictionary<int, PdfStream> _proposalTextLayers = [];

    /// <summary>Text was read in scans during the last run (then the area offers to keep it).</summary>
    public bool HasRecognizedText => _proposalTextLayers.Count > 0 && FieldProposals.Count > 0;

    /// <summary>„Erkannten Text mit ablegen“: with „Übernehmen“ the text read in the scans goes over the pages, as with „Text erkennen“ (on by default).</summary>
    [ObservableProperty]
    public partial bool StoreRecognizedText { get; set; } = true;

    /// <summary>Looks for fields on all pages, where none lie yet, and opens the area with the proposals.</summary>
    /// <param name="openRecognizer">Opens the text recognition for scans (on the working thread); null or returning null: without.</param>
    /// <param name="openDetector">Opens the field detector; null or returning null: without.</param>
    /// <returns>The number of proposals.</returns>
    public async Task<int> RecognizeFieldsAsync(Func<ITextRecognizer?>? openRecognizer = null, Func<IFieldDetector?>? openDetector = null)
    {
        _fieldRecognition?.Cancel();
        var cancel = new CancellationTokenSource();
        _fieldRecognition = cancel;
        ClearFieldProposals();
        IsFieldProposalsOpen = true;
        FieldRecognitionRunning = true;
        var pages = History.Pages.ToArray();
        FieldRecognitionStatus = Tr.T("Suche Felder …");
        FieldRecognitionHint = "";
        _proposalTextLayers = [];
        try
        {
            var names = FieldNames();
            var progress = new Progress<int>(done => FieldRecognitionStatus = Tr.T("Seite {0} von {1} …", Math.Min(done + 1, pages.Length), pages.Length));
            var layers = new Dictionary<int, PdfStream>();
            var scanned = 0;
            ITextRecognizer? recognizer = null;
            IFieldDetector? detector = null;
            var found = await WorkAsync(() => Task.Run(() =>
            {
                try
                {
                    // the models load on this thread (a second or two); one that does not open is left out
                    recognizer = Open(openRecognizer);
                    detector = Open(openDetector);
                    var images = new ImageCache(64L << 20);
                    var inputs = pages.Select((p, i) => new FormRecognizer.PageInput(p.Source, p.Page, [.. p.Elements.Where(IsFormField).Select(e => e.Rect)],
                        c =>
                        {
                            // a scan is looked at in its image even without models: its lines need none
                            if (PageFormScan.Look(p, recognizer, detector, images, c) is not { } look) return null;
                            if (look.Scan.Image is not null) scanned++;
                            if (look.Lines.Count > 0 && OcrTextLayer.Form(look.Lines, look.PageBox) is { } form) layers[i] = form;
                            return look.Scan;
                        })).ToList();
                    var result = FormRecognizer.Recognize(inputs, names, cancel.Token, progress);
                    images.Clear();
                    return result;
                }
                finally
                {
                    recognizer?.Dispose();
                    detector?.Dispose();
                }
            }, cancel.Token));
            _proposalTextLayers = layers;
            FieldRecognitionHint = ModelHint(scanned, recognizer is not null, detector is not null, openRecognizer is not null, openDetector is not null);
            var shared = found.Where(f => f.Spec.Kind != FormFieldKind.Radio).GroupBy(f => (f.Spec.Kind, f.Spec.Name)).Where(g => g.Count() > 1)
                .Select(g => g.Key).ToHashSet();
            foreach (var proposal in found)
            {
                var row = new FieldProposalRow(proposal) { IsLinked = shared.Contains((proposal.Spec.Kind, proposal.Spec.Name)) };
                row.PropertyChanged += (_, _) => FieldProposalsChanged?.Invoke();
                FieldProposals.Add(row);
            }
            FieldRecognitionStatus = found.Count switch
            {
                0 => Tr.T("Keine Felder gefunden — nichts auf den Seiten sieht nach einer Linie oder einem leeren Kästchen zum Ausfüllen aus."),
                1 => Tr.T("1 Feld vorgeschlagen. Prüfen, Namen anpassen, dann übernehmen."),
                _ => Tr.T("{0} Felder vorgeschlagen. Prüfen, Namen anpassen, dann übernehmen.", found.Count),
            };
            FieldProposalsChanged?.Invoke();
            OnPropertyChanged(nameof(HasFieldProposals));
            OnPropertyChanged(nameof(HasRecognizedText));
            return found.Count;
        }
        catch (OperationCanceledException)
        {
            FieldRecognitionStatus = Tr.T("Abgebrochen.");
            return 0;
        }
        finally
        {
            if (ReferenceEquals(_fieldRecognition, cancel)) FieldRecognitionRunning = false;
        }
    }

    /// <summary>Opens a model; one that cannot be opened (missing, damaged) is left out — the recognition works without it.</summary>
    private static T? Open<T>(Func<T?>? open) where T : class
    {
        try
        {
            return open?.Invoke();
        }
        catch (TextRecognitionException ex)
        {
            Core.Platform.AppLog.Write("Modell für „Felder erkennen“ nicht geladen", ex.ToString());
            return null;
        }
    }

    /// <summary>What the models did, or what would help: scans without text recognition have fields without names, and the field detector
    /// finds fields the page does not draw.</summary>
    private static string ModelHint(int scanned, bool read, bool detected, bool canRead, bool canDetect)
    {
        var hints = new List<string>();
        if (read && scanned > 0) hints.Add(scanned == 1 ? Tr.T("Den Text der gescannten Seite hat die Texterkennung gelesen.")
            : Tr.T("Den Text der {0} gescannten Seiten hat die Texterkennung gelesen.", scanned));
        else if (scanned > 0) hints.Add(Tr.T("Gescannte Seiten: Ohne Texterkennung bekommen die Felder keine Namen aus ihrer Beschriftung."));
        if (detected) hints.Add(Tr.T("Der Feld-Erkenner hat mitgesucht."));
        else hints.Add(Tr.T("Mit dem Feld-Erkenner findet die Suche auch Felder, die auf dem Blatt nicht als Linie oder Kästchen gezeichnet sind."));
        if (canRead && !read && scanned > 0 || canDetect && !detected || !canRead && scanned > 0 || !canDetect)
            hints.Add(Tr.T("Ein Modell im Ordner „ocr“ neben dem Programm fehlt oder ließ sich nicht öffnen — bitte PDF-Knead neu entpacken bzw. installieren."));
        return string.Join(" ", hints);
    }

    /// <summary>There are proposals in the list.</summary>
    public bool HasFieldProposals => FieldProposals.Count > 0;

    /// <summary>Ticks all proposals or none.</summary>
    /// <param name="all">True: all.</param>
    public void TickFieldProposals(bool all)
    {
        foreach (var row in FieldProposals) row.IsChecked = all;
    }

    /// <summary>
    /// Takes over the ticked proposals as fields (one undo step) and closes the area; before that, if wanted, the text read in scans goes over
    /// the pages (a step of its own, as with „Text erkennen“).
    /// </summary>
    /// <returns>The number of fields created.</returns>
    public async Task<int> AcceptFieldProposalsAsync()
    {
        if (!CanEditElements) return 0;
        var chosen = FieldProposals.Where(r => r.IsChecked && (uint)r.Proposal.Page < (uint)History.Count).ToList();
        if (chosen.Count == 0) return 0;
        if (StoreRecognizedText && _proposalTextLayers.Count > 0)
        {
            var layers = _proposalTextLayers;
            _proposalTextLayers = [];
            await AddTextLayersAsync(layers, Tr.T("Erkannten Text ablegen"));
        }
        // names of their own, unless they share it on purpose: the options of a group, linked fields (the same label at the same place on
        // other pages, FormRecognizer) — those of one proposed name stay together under the name given in the list
        var taken = FieldNames();
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        string Unique(FieldProposalRow row)
        {
            var wanted = FormRecognizer.Clean(row.Name) is { Length: > 0 } n ? n : row.Proposal.Spec.Name;
            var key = row.Proposal.Spec.Kind + "\u0001" + row.Proposal.Spec.Name + "\u0001" + wanted;
            if (groups.TryGetValue(key, out var group)) return group;
            var name = wanted;
            for (var k = 2; !taken.Add(name); k++) name = $"{wanted}_{k}";
            return groups[key] = name;
        }
        var byPage = new Dictionary<int, IReadOnlyList<PageElement>>();
        foreach (var group in chosen.GroupBy(r => r.Proposal.Page))
            byPage[group.Key] = [.. group.Select(r => Element(r, Unique(r), DisplayRotation(r.Proposal.Page)))];
        History.AddElements(byPage, chosen.Count == 1 ? Tr.T("Erkanntes Feld übernehmen") : Tr.T("Erkannte Felder übernehmen"));
        // the rest goes: a further run proposes again where no field lies yet
        CloseFieldProposals();
        return chosen.Count;
    }

    /// <summary>The field a proposal becomes: a signature field, or a form field with the name from the list.</summary>
    private static PageElement Element(FieldProposalRow row, string name, int rotation)
    {
        var p = row.Proposal;
        if (p.Spec.Kind == FormFieldKind.Signature)
            return new SignatureFieldElement { Rect = p.Rect, Rotation = rotation, FieldName = name, Label = Tr.T("Unterschrift") };
        return new FieldElement
        {
            Rect = p.Rect,
            Rotation = rotation,
            Spec = p.Spec with { Name = name },
            Value = p.Spec.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio ? "Off" : "",
        };
    }

    /// <summary>Closes the area; a running search stops, the proposals go.</summary>
    public void CloseFieldProposals()
    {
        _fieldRecognition?.Cancel();
        ClearFieldProposals();
        IsFieldProposalsOpen = false;
    }

    private void ClearFieldProposals()
    {
        FieldProposals.Clear();
        OnPropertyChanged(nameof(HasFieldProposals));
        OnPropertyChanged(nameof(HasRecognizedText));
        FieldProposalsChanged?.Invoke();
    }

    /// <summary>Shows the page of a proposal.</summary>
    [RelayCommand]
    private void ShowFieldProposal(FieldProposalRow row) => ScrollToPage(row.Proposal.Page);
}
