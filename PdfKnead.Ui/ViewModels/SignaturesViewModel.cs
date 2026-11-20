using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Signatures;

namespace PdfKnead.ViewModels;

/// <summary>Kind of a result for its colour: good, warning (valid but unconfirmed, or changed), bad.</summary>
public enum SignatureTone
{
    Good,
    Warn,
    Bad,
}

/// <summary>One line of the details: label and value.</summary>
public sealed record SignatureDetail(string Label, string Value);

/// <summary>One signature in the „Signaturen“ window.</summary>
public sealed class SignatureRow
{
    /// <param name="check">The result of the check.</param>
    public SignatureRow(SignatureCheck check)
    {
        Check = check;
        Tone = check.Outcome switch
        {
            SignatureOutcome.Valid => SignatureTone.Good,
            SignatureOutcome.ValidIdentityUnknown or SignatureOutcome.NotChecked or SignatureOutcome.Unconfirmed => SignatureTone.Warn,
            _ => SignatureTone.Bad,
        };
        var details = new List<SignatureDetail>();
        if (check.SigningTime is { } time) details.Add(new SignatureDetail(Tr.T("Unterschrieben am"), time.LocalDateTime.ToString("g")));
        if (check.Timestamp is { } stamp)
            details.Add(new SignatureDetail(Tr.T("Zeitstempel"), (stamp.Valid
                ? Tr.T("{0} — bestätigt von {1}", stamp.Time.LocalDateTime.ToString("g"), stamp.Authority.Length > 0 ? stamp.Authority : Tr.T("einem Zeitstempeldienst"))
                : Tr.T("passt nicht zur Signatur")) + (check.TimestampService is { Qualified: true } qualified
                ? " · " + Tr.T("qualifizierter Zeitstempel ({0})", qualified.Service.Territory) : "")));
        if (SignatureTexts.TrustList(check) is { } trustList) details.Add(new SignatureDetail(Tr.T("EU-Vertrauensliste"), trustList));
        if (SignatureTexts.LongTerm(check) is { } longTerm) details.Add(new SignatureDetail(Tr.T("Langzeitprüfung"), longTerm));
        if (SignatureTexts.PadesLevel(check) is { } pades) details.Add(new SignatureDetail(Tr.T("PAdES-Stufe"), pades));
        if (SignatureTexts.RenewBefore(check) is { } renew) details.Add(new SignatureDetail(Tr.T("Schutz erneuern bis spätestens"), renew));
        if (check.Reason is { } reason) details.Add(new SignatureDetail(Tr.T("Grund"), reason));
        if (check.Commitment is { } commitment) details.Add(new SignatureDetail(Tr.T("Zweck"), commitment));
        if (check.Location is { } location) details.Add(new SignatureDetail(Tr.T("Ort"), location));
        if (check.Certification is { } level) details.Add(new SignatureDetail(Tr.T("Zertifiziert"), Tr.T("danach erlaubt: {0}", SignatureTexts.Allowed(level))));
        if (check.Lock is { } fieldLock) details.Add(new SignatureDetail(Tr.T("Sperrt"), SignatureTexts.Locked(fieldLock)));
        details.Add(new SignatureDetail(Tr.T("Feld"), check.FieldName.Length > 0 ? check.FieldName : "–"));
        if (check.Page is { } page) details.Add(new SignatureDetail(Tr.T("Seite"), (page + 1).ToString()));
        if (check.Certificate is { } certificate)
        {
            details.Add(new SignatureDetail(Tr.T("Ausgestellt von"), certificate.SelfSigned ? Tr.T("selbst ausgestellt") : certificate.Issuer));
            details.Add(new SignatureDetail(Tr.T("Zertifikat gültig"), Tr.T("{0} bis {1}", certificate.ValidFrom.LocalDateTime.ToString("d"), certificate.ValidUntil.LocalDateTime.ToString("d"))));
            details.Add(new SignatureDetail(Tr.T("Fingerabdruck"), certificate.Fingerprint));
            details.Add(new SignatureDetail(Tr.T("Stufe"), SignatureTexts.Level(check)!));
            if (SignatureNatures.Of(check) is { } nature && SignatureTexts.Qscd(nature) is { } qscd)
                details.Add(new SignatureDetail(Tr.T("Sichere Signaturerstellungseinheit"), qscd));
        }
        details.Add(new SignatureDetail(Tr.T("Verfahren"), check.Method.Length > 0 ? check.Method : "–"));
        Details = details;
        var notes = new List<string>();
        if (check.UnconfirmedReason is { } unconfirmed) notes.Add(unconfirmed);   // why it is yellow, first
        if (check.Problem is { } problem) notes.Add(problem);
        if (SignatureTexts.IsValid(check))
            notes.Add(check.IdentityConfirmed
                ? Tr.T("Das Zertifikat stammt von einer Stelle, der dieses System vertraut.")
                : Tr.T("Die Signatur ist echt, aber nichts bestätigt, wem das Zertifikat gehört. Vergleichen Sie den Fingerabdruck mit dem, den Ihnen der Unterzeichner nennt."));
        notes.AddRange(check.Warnings);
        notes.AddRange(SignatureTexts.NatureNotes(check));
        if (SignatureTexts.Later(check) is { } later) notes.Add(later);
        notes.AddRange(check.LaterDetails.Select(d => "– " + d));
        // only validation data after it: the calm sentence of SignatureTexts.Later says so already (docs/ABLAUFPLAN.md, 2.1, step 10)
        if (!check.CoversWholeFile && check.Later == LaterChanges.None && SignatureTexts.IsValid(check))
            notes.Add(Tr.T("Unterschrieben wurde eine frühere Fassung der Datei."));
        Notes = notes;
    }

    /// <summary>The result.</summary>
    public SignatureCheck Check { get; }

    /// <summary>Who signed (or the field, if the name is unknown).</summary>
    public string Title => Check.IsDocumentTimestamp
        ? Tr.T("Dokument-Zeitstempel von {0}", Check.SignerName is { Length: > 0 } service ? service : Tr.T("einem Zeitstempeldienst"))
        : Check.SignerName is { Length: > 0 } name ? name : Check.FieldName;

    /// <summary>The overall result in a few words.</summary>
    public string Outcome => SignatureTexts.Outcome(Check);

    /// <summary>The line under the name: „Urteil · Art/Stufe · warum nicht“ (docs/ABLAUFPLAN.md, 9.3.5).</summary>
    public string Line => SignatureTexts.Summary(Check);

    /// <summary>Colour of the result.</summary>
    public SignatureTone Tone { get; }

    public bool IsGood => Tone == SignatureTone.Good;
    public bool IsWarn => Tone == SignatureTone.Warn;
    public bool IsBad => Tone == SignatureTone.Bad;

    /// <summary>Label and value of the details.</summary>
    public IReadOnlyList<SignatureDetail> Details { get; }

    /// <summary>Explanations: problem, trust, later changes.</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>Whether the visible field can be shown.</summary>
    public bool HasPage => Check.Page is not null;
}

/// <summary>
/// The „Signaturen“ window (KONZEPT.md, section 5.17, step S4): every digital signature of the document with its result, the
/// certificate and what changed later; „Anzeigen“ jumps to the page of the visible field.
/// </summary>
public sealed partial class SignaturesViewModel : ObservableObject
{
    private readonly Action<int>? _showPage;

    /// <param name="fileName">Name of the file (for the heading).</param>
    /// <param name="checks">The results; empty if the document has no signatures.</param>
    /// <param name="checking">The check is still running.</param>
    /// <param name="showPage">Shows a page (zero-based) in the document; null = not offered.</param>
    public SignaturesViewModel(string fileName, IReadOnlyList<SignatureCheck> checks, bool checking, Action<int>? showPage)
    {
        FileName = fileName;
        Rows = [.. checks.Select(c => new SignatureRow(c))];
        Checking = checking;
        _showPage = showPage;
    }

    public string FileName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText), nameof(HasRows))]
    public partial IReadOnlyList<SignatureRow> Rows { get; private set; }

    /// <summary>Whether there is a signature (then a document time stamp can protect it).</summary>
    public bool HasRows => Rows.Count > 0;

    public bool Checking { get; private set; }

    /// <summary>Shows the results of a new check (after the EU trusted lists were loaded or removed).</summary>
    /// <param name="checks">The results.</param>
    public void Update(IReadOnlyList<SignatureCheck> checks)
    {
        Checking = false;
        Rows = [.. checks.Select(c => new SignatureRow(c))];
    }

    /// <summary>Opens „EU-Vertrauensliste“ (set by the main window's model, which checks again afterwards); null = not offered.</summary>
    public Func<Task>? OpenTrustLists { get; init; }

    /// <summary>Adds a document time stamp to the file (set by the main window's model, which checks again afterwards); null = not offered.</summary>
    public Func<Task>? AddArchive { get; init; }

    /// <summary>Saves the validation report (set by the main window's model); null = not offered.</summary>
    public Func<Task>? SaveReport { get; init; }

    /// <summary>„Prüfbericht speichern …“ (docs/ABLAUFPLAN.md, 9.3.5, step 4).</summary>
    [RelayCommand]
    private async Task SaveValidationReport()
    {
        if (SaveReport is { } save) await save();
    }

    /// <summary>„Archiv-Zeitstempel hinzufügen“.</summary>
    [RelayCommand]
    private async Task AddArchiveTimestamp()
    {
        if (AddArchive is { } add) await add();
    }

    /// <summary>„EU-Vertrauensliste …“.</summary>
    [RelayCommand]
    private async Task ShowTrustLists()
    {
        if (OpenTrustLists is { } open) await open();
    }

    /// <summary>Text when there is nothing to list.</summary>
    public string? EmptyText => Rows.Count > 0 ? null
        : Checking ? Tr.T("Die Signaturen werden noch geprüft …") : Tr.T("Dieses Dokument hat keine digitalen Signaturen.");

    /// <summary>Raised when the window should close (after „Anzeigen“).</summary>
    public event Action? CloseRequested;

    /// <summary>Shows the page of the visible field and closes the window.</summary>
    [RelayCommand]
    private void ShowPage(SignatureRow? row)
    {
        if (row?.Check.Page is not { } page || _showPage is null) return;
        _showPage(page);
        CloseRequested?.Invoke();
    }
}
