using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.ViewModels;

namespace PdfKnead.EInvoice.Ui;

/// <summary>A line of the readable invoice: a group heading or a term with its value, indented by depth.</summary>
public sealed record InvoiceRow(string Text, bool IsGroup, int Depth)
{
    /// <summary>The indentation.</summary>
    public Avalonia.Thickness Indent => new(Depth * 12, IsGroup ? 6 : 0, 0, 0);
}

/// <summary>
/// A value of the comparison: its status, the term, the value and a note; a found one knows its places — all marked when clicked, each click
/// going to the next (which of several belongs to the field is not said, Bauvorgabe P2).
/// </summary>
public sealed partial class MatchRow(string status, string term, string value, string? note, IReadOnlyList<(int Page, TextQuad Quad)> places,
    Action<IReadOnlyList<(int Page, TextQuad Quad)>, int> reveal)
{
    private int _next;

    public string Status { get; } = status;

    public string Term { get; } = term;

    public string Value { get; } = value;

    public string? Note { get; } = note;

    /// <summary>„S. 2“ for a value found on page 2, „S. 2, 3 (3 Stellen)“ for several places; empty otherwise.</summary>
    public string Page => places.Count switch
    {
        0 => "",
        1 => Tr.T("S. {0}", places[0].Page + 1),
        var n => Tr.T("S. {0} ({1} Stellen)", string.Join(", ", places.Select(p => p.Page + 1).Distinct()), n),
    };

    public bool HasPlace => places.Count > 0;

    /// <summary>Marks all places and brings the next into view (the first at the first click).</summary>
    [RelayCommand]
    private void Reveal()
    {
        if (places.Count == 0) return;
        reveal(places, _next);
        _next = (_next + 1) % places.Count;
    }
}

/// <summary>
/// A row of the box „Zahlung“: what and its value; where the value stands visibly on the page, clicking marks all its places and each further
/// click goes to the next — so the buyer sees whether the amount printed is the amount due (decided on 15.11.2026). Which place belongs
/// to the field is not said (Bauvorgabe P2).
/// </summary>
public sealed partial class PaymentRow(string label, string value, IReadOnlyList<(int Page, TextQuad Quad)> places,
    Action<IReadOnlyList<(int Page, TextQuad Quad)>, int>? reveal)
{
    private int _next;

    public string Label { get; } = label;

    public string Value { get; } = value;

    /// <summary>„S. 2“, „S. 1, 2 (3 Stellen)“ where the value stands on the page; empty otherwise.</summary>
    public string Page => places.Count switch
    {
        0 => "",
        1 => Tr.T("S. {0}", places[0].Page + 1),
        var n => Tr.T("S. {0} ({1} Stellen)", string.Join(", ", places.Select(p => p.Page + 1).Distinct()), n),
    };

    public bool HasPlace => places.Count > 0 && reveal is not null;

    /// <summary>
    /// A row a warning is about (no account in the XML, check digits that fail, not visible in the PDF): in the warning colour where it stands,
    /// the order kept (decided on 15.11.2026).
    /// </summary>
    public bool Warn { get; init; }

    /// <summary>A row without places of its own (such as „in der E-Rechnung nicht vorhanden“).</summary>
    public PaymentRow(string label, string value) : this(label, value, [], null)
    {
    }

    /// <summary>Marks all places and brings the next into view (the first at the first click).</summary>
    [RelayCommand]
    private void Reveal()
    {
        if (!HasPlace) return;
        reveal!(places, _next);
        _next = (_next + 1) % places.Count;
    }
}

/// <summary>A line of „Was heißt das für Sie?“ (E4): what a kind of finding means, in the warning colour where it may cost the input tax.</summary>
public sealed record MeaningRow(string Text, bool Warn);

/// <summary>A violated rule: its identifier, weight, what it means (E4), our description and where (value, place in the XML or the PDF).</summary>
public sealed record RuleRow(string Id, string Weight, string Meaning, string Description, string Where)
{
    public bool HasWhere => Where.Length > 0;
}

/// <summary>A mandatory detail as a line: what, its state in words, and whether it is missing (warning colour).</summary>
public sealed record MandatoryRow(string Text, string State, bool Missing);

/// <summary>A line of the section „Datei“: what and its value.</summary>
public sealed record FileRow(string Label, string Value);

/// <summary>An attachment of the PDF: name, size, type and a warning if it is a program or not what it says; it can be saved (never opened).</summary>
public sealed partial class AttachmentRow(string description, bool dangerous, Func<Task> save)
{
    public string Description { get; } = description;

    /// <summary>A program, or bytes other than the declared type: shown in the warning colour.</summary>
    public bool Dangerous { get; } = dangerous;

    [RelayCommand]
    private Task Save() => save();
}

/// <summary>
/// The area of the checker on the right (KONZEPT.md, section 5.24, „Oberfläche“): checks the e-invoice of the file as saved in the background
/// — finding and reading (stage 1), the readable form (stage 2), the comparison with the visible text (stage 3), the rules of the XML (stage 4)
/// and of the PDF (stage 4, part h) — and shows overview, invoice, comparison, rules, file and attachments. Without an e-invoice it says so,
/// without error colour, and offers to check an XRechnung of its own (a loose XML file). Ends after 60 s or with „Abbrechen“. Never „gültig“:
/// findings or „keine Auffälligkeit gefunden“. Strictly reading: nothing of the file is changed (P4).
/// </summary>
public sealed partial class EInvoicePanelViewModel : ObservableObject
{
    /// <summary>Most lines of the readable invoice shown (an invoice of 100 000 lines would freeze the list; the rest is counted).</summary>
    public const int MaxInvoiceRows = 3000;

    /// <summary>The longest check before it ends by itself (KONZEPT.md, section 5.24, limits: usually under 2 s).</summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(60);

    /// <summary>From this size of the XML a hint: Peppol and the federal receipt platforms take at most 15 MB, attachments included.</summary>
    public const long LargeInvoice = 15L << 20;

    /// <summary>The largest attachment saved or described (its data are read into memory).</summary>
    public const long MaxAttachment = 512L << 20;

    private readonly DocumentViewModel _document;
    private readonly Action _close;
    private CancellationTokenSource? _cancel;

    /// <param name="document">The document checked.</param>
    /// <param name="close">Closes the area.</param>
    public EInvoicePanelViewModel(DocumentViewModel document, Action close)
    {
        (_document, _close) = (document, close);
        Checking = StartCheck(CheckPdf);
    }

    /// <summary>Tests: the check, to wait for.</summary>
    internal Task Checking { get; private set; }

    /// <summary>Tests: the limit of time instead of <see cref="TimeLimit"/>.</summary>
    internal TimeSpan Limit { get; set; } = TimeLimit;

    /// <summary>The fixed note: an aid, never a statement on validity.</summary>
    public string Disclaimer => Tr.T("Eine Hilfe beim Prüfen — keine Aussage über die steuerliche oder rechtliche Gültigkeit. Maßgeblich ist die Rechnung selbst.");

    /// <summary>The fixed note of the readable form: it is not the invoice.</summary>
    public string InvoiceDisclaimer => InvoiceData.Disclaimer;

    [ObservableProperty] public partial bool IsBusy { get; private set; } = true;

    [ObservableProperty] public partial string? Error { get; private set; }

    /// <summary>The check is done and found no e-invoice.</summary>
    [ObservableProperty] public partial bool NoInvoice { get; private set; }

    /// <summary>The check is done and found an e-invoice.</summary>
    [ObservableProperty] public partial bool HasInvoice { get; private set; }

    /// <summary>What is checked when it is not the document: „Eigene Datei: rechnung.xml“; empty for the document.</summary>
    [ObservableProperty] public partial string SourceText { get; private set; } = "";

    /// <summary>The hint for an invoice larger than <see cref="LargeInvoice"/>; empty otherwise.</summary>
    [ObservableProperty] public partial string SizeHint { get; private set; } = "";

    [ObservableProperty] public partial string FormatText { get; private set; } = "";

    [ObservableProperty] public partial string ProfileText { get; private set; } = "";

    [ObservableProperty] public partial string RulesText { get; private set; } = "";

    [ObservableProperty] public partial string CountText { get; private set; } = "";

    /// <summary>„E-Rechnung im Sinne des UStG: ja / nein (Profil)“; empty if the letters of the ministry do not say.</summary>
    [ObservableProperty] public partial string TaxText { get; private set; } = "";

    /// <summary>What was noticed while finding and reading (stage 1).</summary>
    [ObservableProperty] public partial IReadOnlyList<string> Findings { get; private set; } = [];

    /// <summary>The other attachments of the PDF.</summary>
    [ObservableProperty] public partial IReadOnlyList<AttachmentRow> Attachments { get; private set; } = [];

    /// <summary>Whether the section of the attachments is open („Anhänge“ in the tab opens it).</summary>
    [ObservableProperty] public partial bool AttachmentsOpen { get; set; }

    /// <summary>The readable invoice (at most <see cref="MaxInvoiceRows"/> lines).</summary>
    [ObservableProperty] public partial IReadOnlyList<InvoiceRow> InvoiceRows { get; private set; } = [];

    /// <summary>„… und 12 345 weitere Zeilen“ when the readable invoice was cut; empty otherwise.</summary>
    [ObservableProperty] public partial string InvoiceMore { get; private set; } = "";

    /// <summary>Each value against the visible text of the PDF.</summary>
    [ObservableProperty] public partial IReadOnlyList<MatchRow> Matches { get; private set; } = [];

    /// <summary>„12 gefunden, 2 nicht gefunden …“.</summary>
    [ObservableProperty] public partial string MatchSummary { get; private set; } = "";

    /// <summary>The violated rules, the errors first.</summary>
    [ObservableProperty] public partial IReadOnlyList<RuleRow> Rules { get; private set; } = [];

    /// <summary>How the invoice lies in the PDF.</summary>
    [ObservableProperty] public partial IReadOnlyList<FileRow> FileRows { get; private set; } = [];

    /// <summary>The box „Zahlung“ for the buyer (E1): payee, accounts, amount, due date, terms, reference, direct debit.</summary>
    [ObservableProperty] public partial IReadOnlyList<PaymentRow> PaymentRows { get; private set; } = [];

    /// <summary>What to check before paying, in the warning colour: an account or the amount not visible in the PDF, check digits that fail.</summary>
    [ObservableProperty] public partial IReadOnlyList<string> PaymentWarnings { get; private set; } = [];

    /// <summary>Further notes of the box (a payee other than the seller; the PDF without readable text).</summary>
    [ObservableProperty] public partial IReadOnlyList<string> PaymentNotes { get; private set; } = [];

    /// <summary>The mandatory details of § 14 Abs. 4 UStG (E2): each with its state.</summary>
    [ObservableProperty] public partial IReadOnlyList<MandatoryRow> MandatoryRows { get; private set; } = [];

    /// <summary>„Pflichtangaben (§ 14 UStG): alle vorhanden“ or how many are missing.</summary>
    [ObservableProperty] public partial string MandatorySummary { get; private set; } = "";

    /// <summary>„Kleinbetragsrechnung bis 250 € …“ for an invoice of small amount; empty otherwise.</summary>
    [ObservableProperty] public partial string MandatoryNote { get; private set; } = "";

    public bool HasMandatory => MandatoryRows.Count > 0;

    public bool HasMandatoryNote => MandatoryNote.Length > 0;

    /// <summary>Whether a mandatory detail is missing: the header in the warning colour, the section open by itself.</summary>
    [ObservableProperty] public partial bool HasMandatoryMissing { get; private set; }

    /// <summary>Whether the section is open (by itself when something is missing; the user may fold it).</summary>
    [ObservableProperty] public partial bool MandatoryOpen { get; set; }

    /// <summary>The section „Für den Einkauf“: the payment or the mandatory details.</summary>
    public bool HasPurchase => HasPayment || HasMandatory;

    public bool HasPayment => PaymentRows.Count > 0;

    /// <summary>
    /// E4: the invoice details of the XML that stand nowhere visibly in the PDF — number, date, seller, buyer, tax number or VAT ID; at the
    /// very top (the amount and the account say so in the box). Only searched, never what the page holds instead (P2).
    /// </summary>
    [ObservableProperty] public partial string DeviationText { get; private set; } = "";

    public bool HasDeviation => DeviationText.Length > 0;

    /// <summary>E4: what the findings mean for the buyer, kind by kind (BMF letter of 15.10.2025, Rn. 6a, 6b, 35a).</summary>
    [ObservableProperty] public partial IReadOnlyList<MeaningRow> MeaningRows { get; private set; } = [];

    public bool HasMeaning => MeaningRows.Count > 0;

    public bool HasPaymentWarnings => PaymentWarnings.Count > 0;

    public bool HasPaymentNotes => PaymentNotes.Count > 0;

    /// <summary>The headings of the sections, with their counts.</summary>
    public string InvoiceHeader => Tr.T("Rechnung");

    public string MatchHeader => Matches.Count == 0 ? Tr.T("Abgleich mit der PDF") : Tr.T("Abgleich mit der PDF ({0})", Matches.Count);

    public string RulesHeader => Tr.T("Regeln ({0})", Rules.Count);

    public string FileHeader => Tr.T("Datei");

    public string AttachmentsHeader => Tr.T("Anhänge ({0})", Attachments.Count);

    public bool HasFindings => Findings.Count > 0;

    public bool HasAttachments => Attachments.Count > 0;

    public bool HasMore => InvoiceMore.Length > 0;

    /// <summary>The sections of the PDF (comparison, file, attachments): not for a loose XML file.</summary>
    public bool FromPdf => SourceText.Length == 0;

    /// <summary>The comparison and the file: an e-invoice of the PDF.</summary>
    public bool ShowPdfSections => HasInvoice && FromPdf;

    /// <summary>The section of the attachments: the PDF has some, the check is done.</summary>
    public bool ShowAttachments => FromPdf && HasAttachments && !IsBusy;

    /// <summary>What to say when nothing was found: in the PDF, or in the file chosen.</summary>
    public string NoInvoiceText => FromPdf ? Tr.T("In dieser PDF ist keine E-Rechnung eingebettet.")
        : Tr.T("Diese Datei ist keine E-Rechnung, die PDF-Knead kennt (CII, UBL, ZUGFeRD 1).");

    /// <summary>„Keine Anhänge.“: a PDF without attachments, when it holds no e-invoice or „Anhänge“ asked for them.</summary>
    public bool NoAttachmentsNote => FromPdf && !IsBusy && !HasAttachments && Error is null && (NoInvoice || AttachmentsOpen);

    /// <summary>„XRechnung als eigene Datei prüfen …“: when the PDF holds none, and after a loose file (to check the next one).</summary>
    public bool OfferLooseFile => !IsBusy && (NoInvoice || !FromPdf);

    partial void OnHasInvoiceChanged(bool value) => OnPropertyChanged(nameof(ShowPdfSections));

    partial void OnNoInvoiceChanged(bool value)
    {
        OnPropertyChanged(nameof(NoAttachmentsNote));
        OnPropertyChanged(nameof(OfferLooseFile));
    }

    partial void OnFindingsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasFindings));

    partial void OnAttachmentsChanged(IReadOnlyList<AttachmentRow> value)
    {
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(AttachmentsHeader));
        OnPropertyChanged(nameof(ShowAttachments));
        OnPropertyChanged(nameof(NoAttachmentsNote));
    }

    partial void OnInvoiceMoreChanged(string value) => OnPropertyChanged(nameof(HasMore));

    partial void OnPaymentRowsChanged(IReadOnlyList<PaymentRow> value)
    {
        OnPropertyChanged(nameof(HasPayment));
        OnPropertyChanged(nameof(HasPurchase));
    }

    partial void OnMandatoryRowsChanged(IReadOnlyList<MandatoryRow> value)
    {
        OnPropertyChanged(nameof(HasMandatory));
        OnPropertyChanged(nameof(HasPurchase));
    }

    partial void OnMandatoryNoteChanged(string value) => OnPropertyChanged(nameof(HasMandatoryNote));

    partial void OnDeviationTextChanged(string value) => OnPropertyChanged(nameof(HasDeviation));

    partial void OnMeaningRowsChanged(IReadOnlyList<MeaningRow> value) => OnPropertyChanged(nameof(HasMeaning));

    partial void OnPaymentWarningsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasPaymentWarnings));

    partial void OnPaymentNotesChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasPaymentNotes));

    partial void OnMatchesChanged(IReadOnlyList<MatchRow> value) => OnPropertyChanged(nameof(MatchHeader));

    partial void OnRulesChanged(IReadOnlyList<RuleRow> value) => OnPropertyChanged(nameof(RulesHeader));

    partial void OnSourceTextChanged(string value)
    {
        foreach (var name in new[] { nameof(FromPdf), nameof(ShowPdfSections), nameof(ShowAttachments), nameof(NoInvoiceText), nameof(NoAttachmentsNote), nameof(OfferLooseFile) })
            OnPropertyChanged(name);
    }

    partial void OnIsBusyChanged(bool value)
    {
        CancelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowAttachments));
        OnPropertyChanged(nameof(OfferLooseFile));
        OnPropertyChanged(nameof(NoAttachmentsNote));
    }

    partial void OnAttachmentsOpenChanged(bool value) => OnPropertyChanged(nameof(NoAttachmentsNote));

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(NoAttachmentsNote));

    /// <summary>Asks where to save a file (set by the view): the suggested name, the extension without dot; returns the path, or null.</summary>
    public Func<string, string, Task<string?>>? PickSavePath { get; set; }

    /// <summary>Asks for an XML file to check (set by the view); returns the path, or null.</summary>
    public Func<Task<string?>>? PickXmlFile { get; set; }

    /// <summary>Tests: what saving an attachment last reported.</summary>
    internal string? LastMessage { get; private set; }

    [RelayCommand]
    private void Close()
    {
        _cancel?.Cancel();
        _close();
    }

    /// <summary>„Abbrechen“ while checking.</summary>
    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cancel?.Cancel();

    /// <summary>„XRechnung als eigene Datei prüfen …“: an XML file chosen by the user.</summary>
    [RelayCommand]
    private async Task CheckFile()
    {
        if (PickXmlFile is { } pick && await pick() is { } path) CheckLooseFile(path);
    }

    /// <summary>Checks a loose XML file (chosen, or dropped on the area) instead of the document.</summary>
    public void CheckLooseFile(string path) => Checking = StartCheck(cancel => CheckLoose(path, cancel), Tr.T("Eigene Datei: {0}", InvoiceFormat.Plain(Path.GetFileName(path), 80)));

    /// <summary>What the check found, gathered in the background.</summary>
    private sealed record Result(bool HasInvoice, InvoiceKind? Kind, IReadOnlyList<string> Findings, IReadOnlyList<(InvoiceFile File, string Text, bool Dangerous)> Attachments,
        IReadOnlyList<InvoiceRuleViolation> Violations, IReadOnlyList<InvoiceRow> Rows, int RowsLeft, InvoiceComparisonResult? Comparison,
        InvoiceData? Data, IReadOnlyList<FileRow> File, long Size)
    {
        public static Result None(IReadOnlyList<string> findings, IReadOnlyList<(InvoiceFile, string, bool)> attachments) =>
            new(false, null, findings, attachments, [], [], 0, null, null, [], 0);
    }

    /// <summary>Runs a check in the background, ending after the limit of time or with „Abbrechen“.</summary>
    private async Task StartCheck(Func<CancellationToken, Result> run, string source = "")
    {
        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource(Limit);
        Clear(source);
        try
        {
            var result = await Task.Run(() => run(cancel.Token), cancel.Token);
            if (ReferenceEquals(cancel, _cancel)) Show(result);
        }
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(cancel, _cancel)) return;   // a newer check runs
            Error = Tr.T("Die Prüfung wurde abgebrochen — von Hand oder nach {0} Sekunden.", (int)Limit.TotalSeconds);
            IsBusy = false;
        }
        catch (Exception e) when (e is PdfException or IOException or InvalidDataException or UnauthorizedAccessException or InvoiceXmlException)
        {
            if (!ReferenceEquals(cancel, _cancel)) return;
            Error = Tr.T("Die Prüfung ist fehlgeschlagen: {0}", e.Message);
            IsBusy = false;
        }
        finally
        {
            // the timer of the limit is freed; a newer check has its own source (all of this runs on the UI thread, so no race with Cancel)
            if (ReferenceEquals(cancel, _cancel)) _cancel = null;
            cancel.Dispose();
        }
    }

    /// <summary>Back to „Prüfe …“ before a check.</summary>
    private void Clear(string source)
    {
        (IsBusy, Error, NoInvoice, HasInvoice, SourceText, SizeHint) = (true, null, false, false, source, "");
        (FormatText, ProfileText, RulesText, CountText, TaxText) = ("", "", "", "", "");
        (Findings, Attachments, InvoiceRows, InvoiceMore, Matches, MatchSummary, Rules, FileRows) = ([], [], [], "", [], "", [], []);
        (PaymentRows, PaymentWarnings, PaymentNotes) = ([], [], []);
        (MandatoryRows, MandatorySummary, MandatoryNote, HasMandatoryMissing, MandatoryOpen) = ([], "", "", false, false);
        (DeviationText, MeaningRows) = ("", []);
    }

    /// <summary>The whole check of the document on a copy of its file (background thread).</summary>
    private Result CheckPdf(CancellationToken cancel)
    {
        using var copy = _document.OpenSavedCopy();
        if (copy is null) return Result.None([], []);
        var location = InvoiceLocator.Locate(copy, null, cancel);
        var attachments = location.Attachments.Take(200).Select(a => Attachment(copy, a)).ToList();
        var findings = location.Findings.Select(f => f.Text).ToList();
        if (!location.HasInvoice) return Result.None(findings, attachments);
        var file = FileFacts(copy, location);
        var size = location.Data?.LongLength ?? 0;
        if (location is not { Xml: { } xml, Kind: { } kind } || InvoiceReader.Read(xml, kind, cancel) is not { } data)
            return new Result(true, location.Kind, findings, attachments, [], [], 0, null, null, file, size);
        var violations = new List<InvoiceRuleViolation>(InvoiceRules.Check(data, cancel));
        violations.AddRange(InvoiceHybridRules.Check(copy, location, data, cancel));
        var (rows, left) = Readable(data);
        return new Result(true, kind, findings, attachments, violations, rows, left, InvoiceComparison.Compare(copy, data, cancel), data, file, size);
    }

    /// <summary>The check of a loose XML file: reading, readable form, the rules of the XML (no PDF, so no comparison and no rules of the PDF).</summary>
    private static Result CheckLoose(string path, CancellationToken cancel)
    {
        var info = new FileInfo(path);
        if (info.Length > InvoiceXmlLimits.Default.MaxBytes) throw new InvalidDataException(Tr.T("Die Datei ist zu groß ({0} MB).", info.Length >> 20));
        var bytes = File.ReadAllBytes(path);
        var location = InvoiceLocator.Read(bytes, null, cancel);
        var findings = location.Findings.Select(f => f.Text).ToList();
        if (location is not { Xml: { } xml, Kind: { } kind } || kind.Syntax == InvoiceSyntax.Unknown)
            return location.Refused is null ? Result.None(findings, []) : new Result(true, null, findings, [], [], [], 0, null, null, [], bytes.LongLength);
        if (InvoiceReader.Read(xml, kind, cancel) is not { } data) return new Result(true, kind, findings, [], [], [], 0, null, null, [], bytes.LongLength);
        var (rows, left) = Readable(data);
        return new Result(true, kind, findings, [], InvoiceRules.Check(data, cancel), rows, left, null, data, [], bytes.LongLength);
    }

    /// <summary>An attachment as a line: name, size, type, and a warning (read up to <see cref="MaxAttachment"/> to tell what it is).</summary>
    private static (InvoiceFile File, string Text, bool Dangerous) Attachment(Core.Pdf.Document.PdfDocument doc, InvoiceFile file)
    {
        if (InvoiceSources.Data(doc, file, MaxAttachment) is not { } data)
            return (file, $"{InvoiceFormat.Plain(file.DisplayName, 120)} ({Tr.T("nicht lesbar oder zu groß")})", false);
        var text = InvoiceAttachment.Describe(data, file.MimeType, file.DisplayName);
        return (file, text, InvoiceAttachment.Of(data) == InvoiceAttachment.Kind.Program || InvoiceAttachment.Contradicts(data, file.MimeType));
    }

    /// <summary>Saves an attachment, byte for byte, where the user says — never opened (the safe way, Fehlergedächtnis 750).</summary>
    private async Task SaveAttachment(InvoiceFile file)
    {
        if (PickSavePath is not { } pick) return;
        byte[]? data;
        try
        {
            data = await Task.Run(() =>
            {
                using var copy = _document.OpenSavedCopy();
                return copy is null ? null : InvoiceSources.Data(copy, file, MaxAttachment);
            });
        }
        catch (Exception e) when (e is PdfException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Report(Tr.T("Der Anhang ließ sich nicht lesen: {0}", e.Message));
            return;
        }
        if (data is null)
        {
            Report(Tr.T("Der Anhang ließ sich nicht lesen: {0}", Tr.T("nicht lesbar oder zu groß")));
            return;
        }
        // only the name of the file, never a path it may carry
        var name = Path.GetFileName(InvoiceFormat.Plain(file.DisplayName, 120).Replace('\\', '/').Split('/')[^1]);
        var extension = Path.GetExtension(name).TrimStart('.');
        if (await pick(name.Length > 0 ? name : "anhang", extension.Length > 0 ? extension : "bin") is not { } target) return;
        try
        {
            PdfFileSaver.SaveFile(target, stream => stream.Write(data));
            Report(Tr.T("Der Anhang ist gespeichert: {0}", Path.GetFileName(target)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Report(Tr.T("Der Anhang ließ sich nicht speichern: {0}", e.Message));
        }
    }

    private void Report(string message)
    {
        LastMessage = message;
        SaveNote = message;
    }

    /// <summary>The last message of saving an attachment (shown under the attachments).</summary>
    [ObservableProperty] public partial string SaveNote { get; private set; } = "";

    /// <summary>The readable invoice as indented lines (as the command line shows it), at most <see cref="MaxInvoiceRows"/>.</summary>
    private static (List<InvoiceRow> Rows, int Left) Readable(InvoiceData data)
    {
        var rows = new List<InvoiceRow>();
        var left = 0;
        var pending = new Stack<(InvoiceGroup Group, int Depth)>();
        pending.Push((data.Root, 0));
        while (pending.Count > 0)
        {
            var (g, depth) = pending.Pop();
            var lines = (g.Id != "INVOICE" ? 1 : 0) + g.Fields.Count;
            if (rows.Count + lines > MaxInvoiceRows) left += lines;
            else
            {
                if (g.Id != "INVOICE") rows.Add(new InvoiceRow(InvoiceFormat.Plain(g.Label, 120), true, depth));
                foreach (var f in g.Fields)
                    rows.Add(new InvoiceRow($"{InvoiceFormat.Plain(f.Label, 80)}: {InvoiceFormat.Plain(InvoiceFormat.Display(f, data), 200)}", false, depth));
            }
            for (var i = g.Groups.Count - 1; i >= 0; i--) pending.Push((g.Groups[i], depth + 1));
        }
        return (rows, left);
    }

    /// <summary>How the invoice lies in the PDF: the file, its relationship and type, where found, the XMP data, the declared PDF/A.</summary>
    private static List<FileRow> FileFacts(Core.Pdf.Document.PdfDocument doc, InvoiceLocation location)
    {
        var rows = new List<FileRow>();
        if (location.File is { } f)
        {
            rows.Add(new FileRow(Tr.T("Datei"), InvoiceFormat.Plain(f.DisplayName, 80)));
            rows.Add(new FileRow("/AFRelationship", f.Relationship ?? "–"));
            rows.Add(new FileRow(Tr.T("MIME-Typ"), f.MimeType ?? "–"));
            rows.Add(new FileRow(Tr.T("Fundort"), string.Join(", ", f.Places.Select(p => p.Place switch
            {
                InvoiceFilePlace.CatalogAf => Tr.T("/AF des Katalogs"),
                InvoiceFilePlace.EmbeddedFiles => Tr.T("Namensbaum /EmbeddedFiles"),
                _ => Tr.T("Anmerkung auf Seite {0}", p.Page),
            }).Distinct())));
        }
        if (location.Xmp is { Namespace.Length: > 0 } xmp)
        {
            rows.Add(new FileRow("XMP DocumentType", InvoiceFormat.Plain(xmp.DocumentType ?? "–", 80)));
            rows.Add(new FileRow("XMP DocumentFileName", InvoiceFormat.Plain(xmp.DocumentFileName ?? "–", 80)));
            rows.Add(new FileRow("XMP Version", InvoiceFormat.Plain(xmp.Version ?? "–", 80)));
            rows.Add(new FileRow("XMP ConformanceLevel", InvoiceFormat.Plain(xmp.ConformanceLevel ?? "–", 80)));
        }
        else rows.Add(new FileRow("XMP", Tr.T("keine Rechnungsangaben")));
        var declared = PdfACheck.Declared(doc);
        rows.Add(new FileRow("PDF/A", declared is null or PdfAConformance.None ? Tr.T("nicht erklärt")
            : $"PDF/A-{declared.Value.Part()}{declared.Value.Level()?.ToLowerInvariant()}"));
        return rows;
    }

    /// <summary>Shows the result (UI thread).</summary>
    private void Show(Result result)
    {
        Findings = result.Findings;
        Attachments = [.. result.Attachments.Select(a => new AttachmentRow(a.Text, a.Dangerous, () => SaveAttachment(a.File)))];
        if (!result.HasInvoice)
        {
            NoInvoice = true;
            IsBusy = false;
            return;
        }
        if (result.Kind is { } k)
        {
            FormatText = InvoiceNames.Format(k);
            ProfileText = InvoiceNames.Profile(k.Profile);
            TaxText = k.EInvoiceInGermany switch
            {
                true => Tr.T("E-Rechnung im Sinne des UStG: {0}", Tr.T("ja")),
                false => Tr.T("E-Rechnung im Sinne des UStG: {0}", Tr.T("nein ({0})", InvoiceNames.Profile(k.Profile))),
                null => "",
            };
        }
        if (result.Size > LargeInvoice)
            SizeHint = Tr.T("Die Rechnungs-XML ist {0:0.0} MB groß — Peppol und die Rechnungseingangsplattformen nehmen höchstens 15 MB samt Anhängen an.",
                result.Size / 1048576.0);
        RulesText = FromPdf ? $"{InvoiceRules.Source}; {InvoiceHybridRules.Source}" : InvoiceRules.Source;
        var fatal = result.Violations.Count(v => v.Rule.Severity == InvoiceRuleSeverity.Fatal);
        var warnings = result.Violations.Count(v => v.Rule.Severity == InvoiceRuleSeverity.Warning);
        var notes = result.Violations.Count(v => v.Rule.Severity == InvoiceRuleSeverity.Information);
        CountText = result.Violations.Count == 0 ? Tr.T("Keine Auffälligkeit gefunden.") : Counts(fatal, warnings, notes);
        MeaningRows = Meanings(result.Violations, result.Kind?.EInvoiceInGermany != false);
        InvoiceRows = result.Rows;
        InvoiceMore = result.RowsLeft switch
        {
            0 => "",
            1 => Tr.T("… und 1 weitere Zeile"),
            var left => Tr.T("… und {0} weitere Zeilen", left),
        };
        // the weightiest first (E4): format errors, mandatory details, other rules, technical; within by weight
        Rules = [.. result.Violations.OrderBy(InvoiceFindingMeaning.Of).ThenBy(v => v.Rule.Severity).Select(Row)];
        if (result.Comparison is { } comparison && result.Data is { } data)
        {
            var reveal = (IReadOnlyList<(int Page, TextQuad Quad)> places, int current) => _document.RevealPlaces(places, current);
            Matches = [.. comparison.Matches.Select(m => new MatchRow(InvoiceComparison.Describe(m.Status), InvoiceFormat.Plain(m.Field.Label, 80),
                InvoiceFormat.Plain(InvoiceFormat.Display(m.Field, data), 120), m.Note is null ? null : InvoiceFormat.Plain(m.Note, 160),
                [.. m.AllPlaces.Where(p => p.Quads.Count > 0).Select(p => (p.Page - 1, p.Quads[0]))], reveal))];
            MatchSummary = string.Join(", ", Enum.GetValues<InvoiceMatchStatus>().Where(s => comparison.Count(s) > 0)
                .Select(s => $"{comparison.Count(s)} {InvoiceComparison.Describe(s)}"));
        }
        FileRows = result.File;
        if (result.Data is { } invoice)
        {
            Payment(invoice, result.Comparison, result.Kind?.EInvoiceInGermany != false);
            Mandatory(invoice, result.Kind);
            if (result.Comparison is { HasVisibleText: true } shown) Deviations(shown, invoice);
        }
        HasInvoice = true;
        IsBusy = false;
    }

    /// <summary>
    /// The box „Zahlung“ (KONZEPT.md, section 5.24, E1; concept approved by the user 15.11.2026): what the buyer pays to whom, and a warning where an
    /// account or the amount of the XML does not stand visibly in the PDF — the pattern of invoice fraud: the XML changed, the picture not. Only
    /// searched, never read out of the PDF (P2): „another IBAN in the PDF“ is not said.
    /// </summary>
    private void Payment(InvoiceData data, InvoiceComparisonResult? comparison, bool eInvoice = true)
    {
        var payment = InvoicePayment.Of(data);
        if (!payment.HasContent) return;
        string Shown(InvoiceField f, int max = 120) => InvoiceFormat.Plain(InvoiceFormat.Display(f, data), max);
        var issues = payment.Issues(comparison);
        bool Is(InvoicePaymentIssue kind, InvoiceField f) => issues.Any(i => i.Kind == kind && ReferenceEquals(i.Field, f));
        // where a value stands visibly: found (exactly or rounded), or a short number seen only out of its context
        var reveal = (IReadOnlyList<(int Page, TextQuad Quad)> places, int current) => _document.RevealPlaces(places, current);
        IReadOnlyList<(int Page, TextQuad Quad)> Places(InvoiceField f)
        {
            var m = comparison?.Matches.FirstOrDefault(x => ReferenceEquals(x.Field, f));
            var list = m switch
            {
                { Status: InvoiceMatchStatus.Found or InvoiceMatchStatus.Rounded } => m.AllPlaces,
                { VisibleOutOfContext: true } => m.OutOfContextPlaces,
                _ => [],
            };
            return [.. list.Where(p => p.Quads.Count > 0).Select(p => (p.Page - 1, p.Quads[0]))];
        }
        PaymentRow Row(string label, string value, InvoiceField f, bool warn = false) => new(label, value, Places(f), reveal) { Warn = warn };
        var rows = new List<PaymentRow>();
        var warnings = new List<string>();
        var notes = new List<string>();
        if (payment.Payee is { } payee) rows.Add(Row(Tr.T("Zahlungsempfänger"), Shown(payee, 80), payee));
        // no account in the XML though something is to be paid: a warning (decided on 15.11.2026: „mit dem WarnBrush, es ist ja schon
        // ein Fehler“ — a note in grey was overlooked); whether the picture holds one may not be read out (P2)
        // a profile that is no e-invoice (MINIMUM holds no account at all): the visible part is the invoice — its account is the one to check,
        // no warning (found in the collections, 16.11.2026)
        if (!eInvoice && issues.Any(i => i.Kind == InvoicePaymentIssue.NoAccount))
        {
            rows.Add(new PaymentRow("IBAN", Tr.T("im sichtbaren Teil prüfen")));
            notes.Add(Tr.T("Dieses Profil ist keine E-Rechnung im Sinne des UStG — die Bankverbindung prüfen Sie im sichtbaren Teil."));
        }
        else if (issues.Any(i => i.Kind == InvoicePaymentIssue.NoAccount))
        {
            rows.Add(new PaymentRow("IBAN", Tr.T("in der E-Rechnung nicht vorhanden")) { Warn = true });
            warnings.Add(Tr.T("Die E-Rechnung nennt keine Bankverbindung. Eine IBAN, die nur im sichtbaren Teil steht, gehört nicht zur E-Rechnung — prüfen Sie sie vor der Zahlung über einen Weg, den Sie kennen."));
        }
        foreach (var (iban, valid) in payment.Accounts)
        {
            var isIban = InvoicePayment.LooksLikeIban(iban.Text!);
            var grouped = isIban ? InvoicePayment.Grouped(iban.Text!) : InvoiceFormat.Plain(iban.Text!, 40);
            var warn = Is(InvoicePaymentIssue.CheckDigits, iban) || Is(InvoicePaymentIssue.AccountNotVisible, iban);
            rows.Add(isIban ? Row("IBAN", $"{grouped} — {(valid ? Tr.T("Prüfziffer stimmt") : Tr.T("Prüfziffer stimmt nicht"))}", iban, warn)
                : Row(Tr.T("Konto"), $"{grouped} — {Tr.T("keine IBAN")}", iban, warn));
            if (Is(InvoicePaymentIssue.CheckDigits, iban)) warnings.Add(Tr.T("Die Prüfziffer der IBAN {0} stimmt nicht — so ist die Bankverbindung nicht gültig.", grouped));
            if (Is(InvoicePaymentIssue.AccountNotVisible, iban))
                warnings.Add(isIban
                    ? Tr.T("Die IBAN {0} der E-Rechnung steht nicht sichtbar in der PDF. Prüfen Sie die Bankverbindung vor der Zahlung über einen Weg, den Sie kennen — nicht über Angaben aus dieser Rechnung.", grouped)
                    : Tr.T("Das Konto {0} der E-Rechnung steht nicht sichtbar in der PDF. Prüfen Sie die Bankverbindung vor der Zahlung über einen Weg, den Sie kennen — nicht über Angaben aus dieser Rechnung.", grouped));
        }
        if (payment.Bic is { } bic) rows.Add(Row("BIC", Shown(bic, 20), bic));
        // the amounts as the buyer reads them: net (against the order), VAT (input tax), gross, paid already, due
        if (payment.Net is { } net) rows.Add(Row(Tr.T("Netto"), Shown(net), net));
        if (payment.Vat is { } vat) rows.Add(Row(Tr.T("Umsatzsteuer"), Shown(vat), vat));
        if (payment.Gross is { } gross) rows.Add(Row(Tr.T("Brutto"), Shown(gross), gross));
        if (payment.Prepaid is { } prepaid) rows.Add(Row(Tr.T("Bereits gezahlt"), Shown(prepaid), prepaid));
        if (payment.Amount is { } amount)
        {
            rows.Add(Row(Tr.T("Zu zahlen"), Shown(amount), amount, Is(InvoicePaymentIssue.AmountNotVisible, amount)));
            if (Is(InvoicePaymentIssue.AmountNotVisible, amount)) warnings.Add(Tr.T("Der zu zahlende Betrag {0} der E-Rechnung steht nicht sichtbar in der PDF.", Shown(amount)));
        }
        if (payment.Due is { } due) rows.Add(Row(Tr.T("Fällig"), Shown(due), due));
        if (payment.Terms is { } terms) rows.Add(Row(Tr.T("Zahlungsbedingungen"), Shown(terms, 200), terms));
        if (payment.Reference is { } reference) rows.Add(Row(Tr.T("Verwendungszweck"), Shown(reference, 140), reference));
        if (payment.DirectDebit)
        {
            var text = payment.Mandate is { } mandate ? Tr.T("wird abgebucht (Mandat {0})", Shown(mandate, 60)) : Tr.T("wird abgebucht");
            rows.Add(payment.Mandate is { } m ? Row(Tr.T("Lastschrift"), text, m) : new PaymentRow(Tr.T("Lastschrift"), text));
        }
        if (payment.DebitedAccount is { } debited) rows.Add(Row(Tr.T("Abgebucht von"), InvoicePayment.Grouped(debited.Text!), debited));
        if (!payment.PayeeIsSeller && payment.Seller is { } seller) notes.Add(Tr.T("Der Zahlungsempfänger ist nicht der Rechnungssteller ({0}).", Shown(seller, 80)));
        if (comparison is { HasVisibleText: false })
            notes.Add(Tr.T("Ob Bankverbindung und Betrag sichtbar in der PDF stehen, ließ sich nicht prüfen — die PDF hat keinen lesbaren Text."));
        (PaymentRows, PaymentWarnings, PaymentNotes) = (rows, warnings, notes);
    }

    /// <summary>„Auffälligkeiten: 1 Fehler, 2 Warnungen, 1 Hinweis“ — one and many each in its own form (decided on 15.11.2026).</summary>
    internal static string Counts(int fatal, int warnings, int notes) => Tr.T("Auffälligkeiten: {0}", string.Join(", ",
        fatal == 1 ? Tr.T("1 Fehler") : Tr.T("{0} Fehler", fatal),
        warnings == 1 ? Tr.T("1 Warnung") : Tr.T("{0} Warnungen", warnings),
        notes == 1 ? Tr.T("1 Hinweis") : Tr.T("{0} Hinweise", notes)));

    /// <summary>
    /// The mandatory details of § 14 Abs. 4 UStG (KONZEPT.md, section 5.24, E2; concept approved by the user 15.11.2026, built on the design decision of
    /// 16.11.2026): whether each stands in the XML — not whether it is right; the cases of § 14a and the small amounts of § 33 UStDV. Something
    /// missing turns the header orange and opens the section by itself.
    /// </summary>
    private void Mandatory(InvoiceData data, InvoiceKind? kind)
    {
        // a profile that is no e-invoice: the visible part is the invoice and holds the details (analysis of the collections, 15.11.2026)
        if (kind is not null && !InvoiceMandatory.Applies(kind))
        {
            MandatoryRows = [new MandatoryRow(Tr.T("Dieses Profil ist keine E-Rechnung im Sinne des UStG — maßgeblich ist der sichtbare Teil; die Pflichtangaben prüfen Sie dort."),
                "", false)];
            MandatorySummary = Tr.T("Pflichtangaben (§ 14 UStG): im sichtbaren Teil prüfen");
            (MandatoryNote, HasMandatoryMissing, MandatoryOpen) = ("", false, false);
            return;
        }
        var items = InvoiceMandatory.Check(data);
        MandatoryRows = [.. items.Select(i => new MandatoryRow(MandatoryText(i.Key), i.State switch
        {
            MandatoryState.Present => i.Key switch
            {
                "number" => Tr.T("vorhanden — ob fortlaufend, lässt sich an einer Rechnung nicht sehen"),
                "amounts" => Tr.T("vorhanden — vereinbarte Minderungen wie Skonto stehen, falls vereinbart, in den Zahlungsbedingungen"),
                _ => Tr.T("vorhanden"),
            },
            MandatoryState.Missing => Tr.T("fehlt: {0}", string.Join(", ", i.Missing.Select(MandatoryPart))),
            MandatoryState.NotCheckable when i.Missing.Contains("dateInText") =>
                Tr.T("ein Datum steht nur im Text (Bemerkung oder Beschreibung einer Position) — ob es das Leistungsdatum ist, prüfen Sie selbst"),
            MandatoryState.NotCheckable when i.Missing.Contains("code389") =>
                Tr.T("Gutschrift (Rechnungsart 389) — das Wort „Gutschrift“ selbst steht nicht in der XML; ob der Code allein genügt, ist nicht geklärt"),
            MandatoryState.NotCheckable => Tr.T("nicht prüfbar"),
            _ => Tr.T("nicht nötig"),
        }, i.State == MandatoryState.Missing))];
        var missing = items.Count(i => i.State == MandatoryState.Missing);
        // to check oneself: a date only in the text, a self-billed invoice without the word — not the note on keeping the invoice, which no
        // invoice can show (it would stand on every one); the header says so, so that „alle vorhanden“ hides nothing
        var selfCheck = items.Count(i => i.State == MandatoryState.NotCheckable && i.Key != "retention");
        var check = selfCheck switch
        {
            0 => "",
            1 => Tr.T("1 Angabe selbst prüfen"),
            var n => Tr.T("{0} Angaben selbst prüfen", n),
        };
        MandatorySummary = (missing, selfCheck) switch
        {
            (0, 0) => Tr.T("Pflichtangaben (§ 14 UStG): alle vorhanden"),
            (0, _) => Tr.T("Pflichtangaben (§ 14 UStG): nichts fehlt, {0}", check),
            (1, _) => Tr.T("Pflichtangaben (§ 14 UStG): 1 fehlt") + (selfCheck > 0 ? ", " + check : ""),
            var (n, _) => Tr.T("Pflichtangaben (§ 14 UStG): {0} fehlen", n) + (selfCheck > 0 ? ", " + check : ""),
        };
        MandatoryNote = InvoiceMandatory.IsSmallAmount(items) ? Tr.T("Kleinbetragsrechnung bis 250 €: es genügen weniger Angaben (§ 33 UStDV).") : "";
        (HasMandatoryMissing, MandatoryOpen) = (missing > 0, missing > 0);
    }

    /// <summary>What a mandatory detail is, in words.</summary>
    private static string MandatoryText(string key) => key switch
    {
        "seller" => Tr.T("Name und Anschrift des Rechnungsstellers"),
        "buyer" => Tr.T("Name und Anschrift des Leistungsempfängers"),
        "taxId" => Tr.T("Steuernummer oder USt-IdNr. des Rechnungsstellers"),
        "date" => Tr.T("Ausstellungsdatum"),
        "number" => Tr.T("Rechnungsnummer"),
        "items" => Tr.T("Menge und Art der Lieferung oder Leistung"),
        "delivery" => Tr.T("Zeitpunkt der Lieferung oder Leistung"),
        "amounts" => Tr.T("Entgelt nach Steuersätzen aufgeschlüsselt"),
        "taxes" => Tr.T("Steuersatz und Steuerbetrag oder Hinweis auf die Steuerbefreiung"),
        "retention" => Tr.T("Hinweis auf die Aufbewahrungspflicht (nur bei Leistungen an Privatpersonen im Zusammenhang mit einem Grundstück)"),
        "selfBilled" => Tr.T("Angabe „Gutschrift“ (nur bei Gutschriften)"),
        "reverseCharge" => Tr.T("Hinweis „Steuerschuldnerschaft des Leistungsempfängers“"),
        "intraCommunity" => Tr.T("USt-IdNr. beider Seiten (innergemeinschaftliche Lieferung)"),
        _ => key,
    };

    /// <summary>A missing part, in words.</summary>
    private static string MandatoryPart(string id) => id switch
    {
        "BT-27" or "BT-44" => Tr.T("Name"),
        "BT-35" or "BT-50" => Tr.T("Straße"),
        "BT-37" or "BT-52" => Tr.T("Ort"),
        "BT-40" or "BT-55" => Tr.T("Land"),
        "BT-31/BT-32" => Tr.T("Steuernummer oder USt-IdNr."),
        "BT-2" => Tr.T("Datum"),
        "BT-1" => Tr.T("Nummer"),
        "BG-25" => Tr.T("Positionen"),
        "BT-153" => Tr.T("Artikelname je Position"),
        "BT-129" => Tr.T("Menge je Position"),
        "BT-72/BG-14" => Tr.T("Liefer- oder Leistungsdatum"),
        _ when id.StartsWith("BG-26:", StringComparison.Ordinal) && id[6..].Split('/') is [var lacking, var of] =>
            Tr.T("Liefer- oder Leistungsdatum bei {0} von {1} Positionen", lacking, of),
        "BG-23" or "BT-116" => Tr.T("Aufschlüsselung je Steuersatz"),
        "BT-119/BT-117/BT-120" => Tr.T("Steuersatz und Steuerbetrag bzw. Befreiungsgrund"),
        "BT-22" => Tr.T("das Wort „Gutschrift“"),
        "BT-120/BT-121" => Tr.T("Hinweis in der Steuerangabe"),
        "BT-31" => Tr.T("USt-IdNr. des Rechnungsstellers"),
        "BT-48" => Tr.T("USt-IdNr. des Leistungsempfängers"),
        _ => id,
    };

    /// <summary>The details at the top of E4: who issued it, to whom, when, under which number and tax number (in the current language).</summary>
    private static (string Id, string Label)[] Identity() =>
    [
        ("BT-1", Tr.T("Rechnungsnummer")), ("BT-2", Tr.T("Rechnungsdatum")), ("BT-27", Tr.T("Verkäufer")), ("BT-44", Tr.T("Käufer")),
        ("BT-31", Tr.T("USt-IdNr.")), ("BT-32", Tr.T("Steuernummer")),
    ];

    /// <summary>
    /// E4 at the top: the identity of the invoice where it stands nowhere visibly (not found, also not as a short number out of its context).
    /// </summary>
    private void Deviations(InvoiceComparisonResult comparison, InvoiceData data)
    {
        var parts = new List<string>();
        foreach (var (id, label) in Identity())
            if (comparison.Matches.FirstOrDefault(m => m.Field.Id == id) is { Status: InvoiceMatchStatus.NotFound, VisibleOutOfContext: false } match)
                parts.Add($"{label} {InvoiceFormat.Plain(InvoiceFormat.Display(match.Field, data), 60)}");
        DeviationText = parts.Count == 0 ? "" : Tr.T("Nicht sichtbar in der PDF: {0}", string.Join(" · ", parts));
    }

    /// <summary>
    /// „Was heißt das für Sie?“ — one line per kind of finding, the weightiest first, with the place in the BMF letter of 15.10.2025 (read in
    /// the letter itself, 16.11.2026); the kinds that may cost the input tax in the warning colour. A profile that is no e-invoice in the sense
    /// of the UStG (MINIMUM, BASIC WL, ZUGFeRD 1) is an other invoice anyway: the visible part counts, the findings concern only the data
    /// attached — said first, nothing in the warning colour, no „counts as an other invoice“ (found in the collections, 16.11.2026).
    /// </summary>
    internal static IReadOnlyList<MeaningRow> Meanings(IReadOnlyList<InvoiceRuleViolation> violations, bool eInvoice = true)
    {
        var rows = new List<MeaningRow>();
        if (violations.Count == 0) return rows;
        if (!eInvoice)
            rows.Add(new MeaningRow(Tr.T("Dieses Profil ist keine E-Rechnung im Sinne des UStG — maßgeblich ist der sichtbare Teil; die Befunde betreffen nur die beigefügten Rechnungsdaten."), false));
        foreach (var group in violations.GroupBy(InvoiceFindingMeaning.Of).OrderBy(g => g.Key))
        {
            var n = group.Count();
            string Concern() => n == 1 ? Tr.T("1 Auffälligkeit betrifft") : Tr.T("{0} Auffälligkeiten betreffen", n);
            rows.Add(group.Key switch
            {
                InvoiceMeaning.Format when !eInvoice => new MeaningRow(Tr.T("{0}: Die beigefügten Rechnungsdaten entsprechen nicht ihrem Schema.",
                    n == 1 ? Tr.T("1 Formatfehler") : Tr.T("{0} Formatfehler", n)), false),
                InvoiceMeaning.Format => new MeaningRow(Tr.T("{0}: Die Datei entspricht nicht dem Format einer E-Rechnung — sie gilt dann als sonstige Rechnung, nicht als E-Rechnung (BMF-Schreiben vom 15.10.2025, Rn. 6a). Fordern Sie beim Absender eine korrigierte Rechnung an.",
                    n == 1 ? Tr.T("1 Formatfehler") : Tr.T("{0} Formatfehler", n)), true),
                InvoiceMeaning.MandatoryDetail when !eInvoice => new MeaningRow(Tr.T("{0} eine Pflichtangabe in den beigefügten Daten — prüfen Sie sie im sichtbaren Teil.", Concern()), false),
                InvoiceMeaning.MandatoryDetail => new MeaningRow(Tr.T("{0} eine Pflichtangabe nach § 14 Abs. 4 oder § 14a UStG — ein solcher Fehler macht die Rechnung nicht ordnungsgemäß und kann den Vorsteuerabzug gefährden (Rn. 35a). Fordern Sie eine berichtigte Rechnung an.", Concern()), true),
                InvoiceMeaning.BusinessRule => new MeaningRow(Tr.T("{0} andere Geschäftsregeln — umsatzsteuerlich unbeachtlich, solange keine Pflichtangabe betroffen ist (Rn. 35a); meist ein Fehler im Programm des Absenders.", Concern()), false),
                _ => new MeaningRow(Tr.T("{0} technische Angaben (PDF-Hülle, Metadaten, Profil) — solange sich die Rechnungsdaten sicher entnehmen lassen, für den Vorsteuerabzug meist ohne Bedeutung.", Concern()), false),
            });
        }
        return rows;
    }

    /// <summary>What a kind of finding is, as a word in the list.</summary>
    private static string MeaningLabel(InvoiceMeaning kind) => kind switch
    {
        InvoiceMeaning.Format => Tr.T("Formatfehler"),
        InvoiceMeaning.MandatoryDetail => Tr.T("Pflichtangabe"),
        InvoiceMeaning.BusinessRule => Tr.T("Geschäftsregel"),
        _ => Tr.T("technisch"),
    };

    /// <summary>A violation as a line: identifier, weight, what it means, description, and where.</summary>
    private static RuleRow Row(InvoiceRuleViolation v)
    {
        var weight = v.Rule.Severity switch
        {
            InvoiceRuleSeverity.Fatal => Tr.T("Fehler"),
            InvoiceRuleSeverity.Warning => Tr.T("Warnung"),
            _ => Tr.T("Hinweis"),
        };
        var where = new List<string>();
        if (v.Field is { } field) where.Add($"{InvoiceFormat.Plain(field.Label, 60)} ({field.Id})");
        if (v.Place is { } place) where.Add(InvoiceFormat.Plain(place, 120));
        if (v.Value is { } value) where.Add($"„{InvoiceFormat.Plain(value, 80)}“");
        if (v.Further > 0) where.Add(Tr.T("und {0} weitere", v.Further));
        return new RuleRow(v.Rule.Id, weight, MeaningLabel(InvoiceFindingMeaning.Of(v)), v.Rule.Description, string.Join(" — ", where));
    }
}
