using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>
/// File → Export → „Als PDF/A speichern …“ (KONZEPT.md, section 5.21): the part and level (PDF/A-1a … 4e), what changes in the copy, then writing
/// and checking in the background. On opening every part is written and checked once (<see cref="ProbeAsync"/>): the left column lists those
/// this file can meet, one marked as fitting it best and chosen, the right column those it cannot, each with the reason. A copy that does not
/// conform is not saved unless the user asks for it („trotzdem speichern“, with a warning; decided on 5.11.2026). Level a (tagged)
/// asks for the language and, per figure, an alternative text or „nur Schmuck“.
/// </summary>
public sealed partial class PdfAViewModel : ObservableObject
{
    private readonly Func<PdfAConformance, PdfAAccessibility?, Task<PdfAResult>> _write;
    private readonly Func<Task<(string Language, IReadOnlyList<(PdfAFigure Figure, RasterArea? Picture)> Figures)>>? _figures;
    private readonly bool _hasAttachments;
    private readonly bool _large;
    private bool _figuresLoaded;
    // one copy is written at a time: the probe and the user's check read the same document
    private readonly SemaphoreSlim _gate = new(1, 1);
    // conforming copies of the probe (not level a, whose answers come later), reused when the user saves that part
    private readonly Dictionary<PdfAConformance, PdfAResult> _probed = [];
    private bool _userChose, _checkStarted, _syncing;
    private const int MaxKeptBytes = 64 * 1024 * 1024;
    /// <summary>From this size of the file on, the probe stops at the first fitting part (eleven copies of 300 MB take minutes).</summary>
    public const long LargeFile = 50L * 1024 * 1024;

    /// <param name="write">Writes and checks the copy in the background (the part, and for level a the answers).</param>
    /// <param name="notes">What changes in the copy beyond what every PDF/A copy loses (protection, signatures), each one sentence.</param>
    /// <param name="figures">Level a: the proposed language and the figures to ask about, with their pictures; null: none.</param>
    /// <param name="hasAttachments">The document has attached files (the parts that keep them fit it best).</param>
    /// <param name="fileSize">The size of the file in bytes: from <see cref="LargeFile"/> on the probe stops at the first fitting part.</param>
    /// <param name="advice">A module's advice: its sentence at the top, its part chosen first and marked as fitting if the file meets it.</param>
    public PdfAViewModel(Func<PdfAConformance, PdfAAccessibility?, Task<PdfAResult>> write, IReadOnlyList<string> notes,
        Func<Task<(string Language, IReadOnlyList<(PdfAFigure Figure, RasterArea? Picture)> Figures)>>? figures = null, bool hasAttachments = false,
        long fileSize = 0, Modules.PdfAAdvice? advice = null)
    {
        _write = write;
        Advice = advice?.Note;
        _preferred = advice?.Preferred;
        _figures = figures;
        _hasAttachments = hasAttachments;
        _large = fileSize >= LargeFile;
        Notes = notes;
        Options =
        [
            // by part and level (decided on 7.11.2026): 1a, 1b, 2a, 2b, 2u, 3a, 3b, 3u, 4, 4e, 4f
            new(this, PdfAConformance.A1a, Tr.T("PDF/A-1a — erste Fassung, barrierearm"), Tr.T("Wie PDF/A-1b und mit einer Struktur für Vorleseprogramme wie bei PDF/A-2a.")),
            new(this, PdfAConformance.A1b, Tr.T("PDF/A-1b — erste Fassung (PDF 1.4)"), Tr.T("Für Archive, die nur die erste Fassung von 2005 annehmen. Sie kennt keine Durchsichtigkeit, keine Ebenen und keine Anhänge: Durchscheinendes kommt als Bild in die Seite, verborgene Ebenen und Anhänge entfallen.")),
            new(this, PdfAConformance.A2a, Tr.T("PDF/A-2a — barrierearm (getaggt)"), Tr.T("Wie PDF/A-2u und mit einer Struktur für Vorleseprogramme: Überschriften, Absätze, Listen und Bilder in Lesereihenfolge, die Sprache, für jedes Bild ein kurzer Text.")),
            new(this, PdfAConformance.A2b, Tr.T("PDF/A-2b — für Archiv und Behörden"), Tr.T("Die übliche Fassung für Archive und Ämter. Anhänge entfallen.")),
            new(this, PdfAConformance.A2u, Tr.T("PDF/A-2u — Text durchsuchbar"), Tr.T("Wie PDF/A-2b; zusätzlich lässt sich jeder Buchstabe suchen und kopieren. Anhänge entfallen.")),
            new(this, PdfAConformance.A3a, Tr.T("PDF/A-3a — barrierearm, mit Anhängen"), Tr.T("Wie PDF/A-2a und behält angehängte Dateien.")),
            new(this, PdfAConformance.A3b, Tr.T("PDF/A-3b — mit Anhängen"), Tr.T("Behält angehängte Dateien, etwa die Rechnungsdaten einer elektronischen Rechnung.")),
            new(this, PdfAConformance.A3u, Tr.T("PDF/A-3u — Text durchsuchbar, mit Anhängen"), Tr.T("Wie PDF/A-3b; zusätzlich lässt sich jeder Buchstabe suchen und kopieren.")),
            new(this, PdfAConformance.A4, Tr.T("PDF/A-4 — neueste Fassung (PDF 2.0)"), Tr.T("Die neueste Fassung von 2020. Ältere Archivprogramme kennen sie noch nicht. Anhänge entfallen.")),
            new(this, PdfAConformance.A4e, Tr.T("PDF/A-4e — neueste Fassung, Technik (3D)"), Tr.T("Wie PDF/A-4 für technische Unterlagen: behält 3D-Modelle (U3D, PRC), Rich-Media-Inhalte und angehängte Dateien. Skripte darin führt PDF-Knead nie aus.")),
            new(this, PdfAConformance.A4f, Tr.T("PDF/A-4f — neueste Fassung, mit Anhängen"), Tr.T("Wie PDF/A-4 und behält angehängte Dateien. Nur für Dokumente, die mindestens einen Anhang haben.")),
        ];
        foreach (var option in Options) Possible.Add(option);
        Options.First(o => o.Part == (_preferred ?? PdfAConformance.A2b)).IsSelected = true;   // the usual one (or the module's), wherever it stands
        // what is certain without writing: PDF/A-4f without an attachment (ISO 19005-4, Annex A) — at once in the right column
        if (!hasAttachments && Options.First(o => o.Part == PdfAConformance.A4f) is var fourF)
        {
            fourF.Note = Tr.T("PDF/A-4f verlangt mindestens einen Anhang (/EmbeddedFiles).");
            fourF.Fits = false;
            fourF.Probed = true;
            Possible.Remove(fourF);
            InOrder(NotPossible, fourF);
        }
        foreach (var option in Options.Where(o => !o.Probed)) option.Waiting = true;
    }

    /// <summary>How far the probe is, at once on opening („3 von 11 geprüft“).</summary>
    public string Progress => Tr.T("{0} von {1} Fassungen geprüft", Options.Count(o => o.Probed), Options.Count);

    /// <summary>Every part, in the order the window shows them.</summary>
    public IReadOnlyList<PdfAOption> Options { get; }

    /// <summary>The left column: the parts this file can meet (and, while the probe runs, those not checked yet).</summary>
    public ObservableCollection<PdfAOption> Possible { get; } = [];

    /// <summary>The right column: the parts this file cannot meet, each with the reason.</summary>
    public ObservableCollection<PdfAOption> NotPossible { get; } = [];

    /// <summary>The right column has entries.</summary>
    public bool HasNotPossible => NotPossible.Count > 0;

    /// <summary>Puts an option into a column at its place in <see cref="Options"/> (the columns stay sorted by part and level).</summary>
    private void InOrder(ObservableCollection<PdfAOption> column, PdfAOption option)
    {
        var rank = Rank(option);
        var at = 0;
        while (at < column.Count && Rank(column[at]) < rank) at++;
        column.Insert(at, option);
    }

    private int Rank(PdfAOption option)
    {
        for (var i = 0; i < Options.Count; i++)
            if (ReferenceEquals(Options[i], option)) return i;
        return Options.Count;
    }

    /// <summary>The parts are being written and checked once (<see cref="ProbeAsync"/>).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ProbeAllCommand))]
    public partial bool Probing { get; set; }

    /// <summary>A large file: only the fitting parts were tried (until one passed); the others can be checked on request.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ProbeAllCommand))]
    public partial bool PartlyProbed { get; set; }

    private bool CanProbeAll() => PartlyProbed && !Probing;

    /// <summary>A large file: writes and checks the parts the probe left out.</summary>
    [RelayCommand(CanExecute = nameof(CanProbeAll))]
    private Task ProbeAll() => ProbeAsync(all: true);

    /// <summary>The part that fits the file best (null until known): with attachments 3u, else 3b, 4f; without 2u, else 2b, 4.</summary>
    [ObservableProperty]
    public partial PdfAConformance? Recommended { get; set; }

    /// <summary>
    /// Writes and checks every part once, the fitting ones first, and sorts them into the two columns; level a with every figure as decoration
    /// (whether the file can meet it; the answers are the user's). Chooses the fitting part unless the user chose or checked already.
    /// </summary>
    public async Task ProbeAsync(CancellationToken cancel = default) => await ProbeAsync(all: !_large, cancel);

    private CancellationToken _probeCancel;

    private async Task ProbeAsync(bool all, CancellationToken cancel = default)
    {
        if (cancel.CanBeCanceled) _probeCancel = cancel;
        else cancel = _probeCancel;
        Probing = true;
        try
        {
            PdfAConformance[] fitting = _hasAttachments ? [PdfAConformance.A3u, PdfAConformance.A3b, PdfAConformance.A4f] : [PdfAConformance.A2u, PdfAConformance.A2b, PdfAConformance.A4];
            // a module's part first (decided on 15.11.2026: PDF/A-3b for an e-invoice)
            if (_preferred is { } preferred) fitting = [preferred, .. fitting.Where(f => f != preferred)];
            foreach (var option in Options.OrderBy(o => Array.IndexOf(fitting, o.Part) is var i and >= 0 ? i : fitting.Length))
            {
                cancel.ThrowIfCancellationRequested();
                if (option.Probed) continue;
                // a large file: the fitting parts until one passes; the others on request (ProbeAll)
                if (!all && Recommended is not null)
                {
                    option.Note ??= Tr.T("nicht vorab geprüft (große Datei) — wird beim Speichern geprüft");
                    option.Waiting = false;
                    PartlyProbed = true;
                    continue;
                }
                option.Waiting = false;
                option.Pending = true;
                PdfAResult? result = null;
                string? failure = null;
                await _gate.WaitAsync(cancel);
                try
                {
                    result = await _write(option.Part, option.Part.Accessible() ? new PdfAAccessibility { AllDecorative = true } : null);
                }
                catch (Exception ex) when (Failure.Handled(ex))
                {
                    failure = Failure.Text(ex);
                }
                finally
                {
                    _gate.Release();
                    option.Pending = false;
                }
                option.Probed = true;
                OnPropertyChanged(nameof(Progress));
                if (result is not null) option.Size = DocumentViewModel.FormatSize(result.Bytes.Length);
                if (result is { Conforms: true })
                {
                    option.Note = option.Part.Accessible() ? Tr.T("mit Ihren Angaben zu Sprache und Bildern") : null;
                    // kept for saving: small copies, and the fitting one of any size (one copy at most of a large file)
                    if (!option.Part.Accessible() && (result.Bytes.Length <= MaxKeptBytes || Recommended is null && Array.IndexOf(fitting, option.Part) >= 0))
                        _probed[option.Part] = result;
                    if (Recommended is null && Array.IndexOf(fitting, option.Part) >= 0)
                    {
                        Recommended = option.Part;
                        option.IsRecommended = true;
                        if (!_userChose && !_checkStarted) Choose(option.Part);
                    }
                    continue;
                }
                option.Note = failure ?? Short(result!.Violations, option.Part);
                option.Fits = false;
                Possible.Remove(option);
                InOrder(NotPossible, option);
                OnPropertyChanged(nameof(HasNotPossible));
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Probing = false;
            if (all) PartlyProbed = false;
        }
    }

    /// <summary>Why a part does not fit, in one line: the first reason, and how many more rules.</summary>
    private static string Short(IReadOnlyList<PdfAViolation> violations, PdfAConformance part)
    {
        var first = violations[0];
        var more = violations.Select(v => v.Rule).Distinct().Count() - 1;
        return more > 0 ? Tr.T("{0} (und {1} weitere Regeln)", first.Text, more) : first.Text;
    }

    /// <summary>Chooses a part as the window does (the radio buttons follow).</summary>
    private void Choose(PdfAConformance part)
    {
        _syncing = true;
        try
        {
            Part = part;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>A radio button was checked (by the user, or following <see cref="Part"/>).</summary>
    internal void Chosen(PdfAOption option)
    {
        if (Part == option.Part) return;
        if (!_syncing) _userChose = true;
        Part = option.Part;
    }

    /// <summary>Level a is chosen: the window asks for the language and the figures.</summary>
    public bool Accessible => Part.Accessible();

    /// <summary>Level a: the language of the document as a language tag (proposed from its text).</summary>
    [ObservableProperty]
    public partial string Language { get; set; } = "";

    /// <summary>Level a: the figures that need an alternative text or „nur Schmuck“.</summary>
    public ObservableCollection<PdfAFigureItem> Figures { get; } = [];

    /// <summary>The figures are being looked for.</summary>
    [ObservableProperty]
    public partial bool LoadingFigures { get; set; }

    /// <summary>The figures are known and there are none.</summary>
    [ObservableProperty]
    public partial bool NoFigures { get; set; }

    /// <summary>Every figure without an answer counts as decoration.</summary>
    [ObservableProperty]
    public partial bool OthersDecorative { get; set; }

    partial void OnPartChanged(PdfAConformance value)
    {
        OnPropertyChanged(nameof(Accessible));
        foreach (var option in Options) option.IsSelected = option.Part == value;
        Failed = false;
        Reasons.Clear();
        SaveAnywayCommand.NotifyCanExecuteChanged();
        if (value.Accessible()) _ = LoadFiguresAsync();
    }

    /// <summary>Looks for the figures once (level a chosen for the first time).</summary>
    private async Task LoadFiguresAsync()
    {
        if (_figuresLoaded || _figures is null) return;
        _figuresLoaded = true;
        LoadingFigures = true;
        try
        {
            var (language, figures) = await _figures();
            if (Language.Length == 0) Language = language;
            foreach (var (figure, picture) in figures) Figures.Add(new PdfAFigureItem(figure, picture));
            NoFigures = Figures.Count == 0;
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Reasons.Add(Failure.Text(ex));
            Failed = true;
        }
        finally
        {
            LoadingFigures = false;
        }
    }

    /// <summary>The answers given for level a; null for the other levels.</summary>
    internal PdfAAccessibility? Accessibility() => !Accessible ? null : new PdfAAccessibility
    {
        Language = Language.Trim(),
        Figures = Figures.Where(f => f.Decorative || f.AltText.Trim().Length > 0)
            .ToDictionary(f => f.Figure.Key, f => f.Decorative ? null : f.AltText.Trim(), StringComparer.Ordinal),
        AllDecorative = OthersDecorative,
    };

    /// <summary>What changes in the copy (password protection, digital signatures).</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>A module's advice, shown at the top; null: none.</summary>
    public string? Advice { get; }

    public bool HasAdvice => Advice is not null;

    private readonly PdfAConformance? _preferred;

    /// <summary>There is something in <see cref="Notes"/>.</summary>
    public bool HasNotes => Notes.Count > 0;

    /// <summary>The part and level written: PDF/A-2b until the probe finds the fitting one or the user chooses.</summary>
    [ObservableProperty]
    public partial PdfAConformance Part { get; set; } = PdfAConformance.A2b;

    /// <summary>Writing and checking is running.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAnywayCommand))]
    public partial bool Checking { get; set; }

    /// <summary>Why the copy is not a PDF/A file: one line per rule, with the page.</summary>
    public ObservableCollection<string> Reasons { get; } = [];

    /// <summary>The copy does not conform (<see cref="Reasons"/> says why).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAnywayCommand))]
    public partial bool Failed { get; set; }

    /// <summary>The conforming copy, once there is one.</summary>
    public PdfAResult? Result { get; private set; }

    /// <summary>The copy of the last check, also one that does not conform (for the leak probe, which looks at everything the writer produces).</summary>
    public PdfAResult? LastResult { get; private set; }

    /// <summary>A conforming copy is ready (the window closes with it).</summary>
    public event Action<PdfAResult>? Ready;

    private bool CanCheck() => !Checking;

    /// <summary>Writes and checks the copy.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task Check()
    {
        _checkStarted = true;
        Checking = true;
        Failed = false;
        Reasons.Clear();
        var part = Part;
        try
        {
            PdfAResult result;
            // a copy of the probe for this part is the same copy (not level a: its answers are given now)
            if (!part.Accessible() && _probed.TryGetValue(part, out var probed)) result = probed;
            else
            {
                await _gate.WaitAsync();
                try
                {
                    result = await _write(part, Accessibility());
                }
                finally
                {
                    _gate.Release();
                }
            }
            LastResult = result;
            LastPart = part;
            if (result.Conforms)
            {
                Result = result;
                Ready?.Invoke(result);
                return;
            }
            foreach (var v in result.Violations) Reasons.Add(Line(v, part));
            Failed = true;
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Reasons.Add(Failure.Text(ex));
            Failed = true;
        }
        finally
        {
            Checking = false;
        }
    }

    /// <summary>The part of <see cref="LastResult"/>.</summary>
    public PdfAConformance? LastPart { get; private set; }

    private bool CanSaveAnyway() => Failed && !Checking && LastResult is { Conforms: false } && LastPart == Part;

    /// <summary>
    /// Saves the copy that does not conform anyway (decided on 5.11.2026): it carries the PDF/A identification but breaks the rules
    /// the window lists — the message after saving says so.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveAnyway))]
    private void SaveAnyway()
    {
        if (LastResult is { Conforms: false } result) Ready?.Invoke(result);
    }

    /// <summary>A reason as the window shows it: page, what is wrong, and the rule of the standard.</summary>
    internal static string Line(PdfAViolation v, PdfAConformance part) =>
        (v.Page is { } page ? Tr.T("Seite {0}: ", page) : "") + v.Text + " " + Tr.T("(ISO 19005-{0}, {1})", part.Part(), v.Rule);
}

/// <summary>A part in the window: its button, whether this file can meet it, why not, whether it fits the file best.</summary>
public sealed partial class PdfAOption : ObservableObject
{
    private readonly PdfAViewModel _owner;

    internal PdfAOption(PdfAViewModel owner, PdfAConformance part, string title, string tip)
    {
        _owner = owner;
        Part = part;
        Title = title;
        Tip = tip;
    }

    /// <summary>The part and level.</summary>
    public PdfAConformance Part { get; }

    /// <summary>The text of its button.</summary>
    public string Title { get; }

    /// <summary>What it is for.</summary>
    public string Tip { get; }

    /// <summary>Its button is checked.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _owner.Chosen(this);
    }

    /// <summary>The file can meet it (as far as the probe knows: true until checked).</summary>
    [ObservableProperty]
    public partial bool Fits { get; set; } = true;

    /// <summary>It is being written and checked.</summary>
    [ObservableProperty]
    public partial bool Pending { get; set; }

    /// <summary>The probe wrote and checked it.</summary>
    public bool Probed { get; set; }

    /// <summary>Waiting for the probe (shown at once on opening).</summary>
    [ObservableProperty]
    public partial bool Waiting { get; set; }

    /// <summary>The size of the copy the probe wrote („2,3 MB“); null until then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSize))]
    public partial string? Size { get; set; }

    /// <summary>There is a <see cref="Size"/>.</summary>
    public bool HasSize => Size is not null;

    /// <summary>The part that fits the file best.</summary>
    [ObservableProperty]
    public partial bool IsRecommended { get; set; }

    /// <summary>Why the file cannot meet it, or what it needs (level a); null for nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; set; }

    /// <summary>There is a <see cref="Note"/>.</summary>
    public bool HasNote => Note is not null;
}

/// <summary>A figure in the window for level a: where it is, its picture, the alternative text or „nur Schmuck“.</summary>
public sealed partial class PdfAFigureItem : ObservableObject
{
    /// <param name="figure">The figure.</param>
    /// <param name="picture">Its picture (RGB); null if none could be drawn.</param>
    public PdfAFigureItem(PdfAFigure figure, RasterArea? picture)
    {
        Figure = figure;
        Picture = picture;
        Place = figure.Page >= 0 ? Tr.T("Bild auf Seite {0}", figure.Page + 1) : Tr.T("Bild");
    }

    /// <summary>The figure.</summary>
    public PdfAFigure Figure { get; }

    /// <summary>Its picture as RGB pixels (the view makes an image of it); null if none.</summary>
    public RasterArea? Picture { get; }

    /// <summary>Where it is („Bild auf Seite 3“).</summary>
    public string Place { get; }

    /// <summary>What the figure shows, in a few words.</summary>
    [ObservableProperty]
    public partial string AltText { get; set; } = "";

    /// <summary>The figure is only decoration (assistive programs skip it).</summary>
    [ObservableProperty]
    public partial bool Decorative { get; set; }
}
