using PdfKnead.Core.Pdf.Signatures;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Platform;
using PdfKnead.Core.Platform.Pkcs11;

namespace PdfKnead.ViewModels;

/// <summary>
/// The „Zertifikat“ window (KONZEPT.md, section 5.17, step S2; layout after the decision of 11.11.2026, docs/FEHLERGEDAECHTNIS.md,
/// No. 733): time stamps and long-term validation first (they apply to every certificate), the certificate that signs now, a description of the
/// kinds of certificates, then one group each for the own certificates (several, each in its own row), certificate files (several, each with
/// its own password, asked when signing), the signature card and the Windows store. A file's password is only used to check the file here and
/// is never stored. Closing with something left half done shows what it is first.
/// </summary>
public sealed partial class CertificateViewModel : ObservableObject, IDisposable
{
    private readonly string? _ownFile;
    private SigningCertificate? _shown;
    private IReadOnlyList<SigningCertificate>? _store;

    /// <summary>The own certificate the open password, export or removal panel works on.</summary>
    private string? _target;

    /// <param name="choice">The current choice from the settings; null = the own certificate.</param>
    /// <param name="suggestedName">Name for a new own certificate (the name of the last signature).</param>
    /// <param name="ownFile">File of the first own certificate (further ones lie beside it); null = the default (tests supply their own).</param>
    /// <param name="timestamps">Time stamps are switched on.</param>
    /// <param name="timestampService">The time-stamping authority entered before; null: none yet, the suggested free one is entered (switched off).</param>
    /// <param name="longTerm">Long-term validation is switched on.</param>
    /// <param name="archive">Document time stamps after signing are switched on.</param>
    /// <param name="files">The certificate files remembered in the settings; null: none.</param>
    public CertificateViewModel(SigningCertificateChoice? choice, string? suggestedName, string? ownFile = null, bool timestamps = false,
        TimestampService? timestampService = null, bool longTerm = false, bool archive = false, IReadOnlyList<SigningCertificateChoice>? files = null)
    {
        Files = [.. files ?? []];
        // an archive time stamp needs the long-term validation (B-LTA needs B-LT): a setting from before with only the former keeps both (No. 527)
        UseLongTerm = longTerm || archive;
        UseArchive = archive;
        _ownFile = ownFile;
        UseTimestamps = timestamps;
        TimestampServer = timestampService?.Server ?? HttpTimestampAuthority.Suggested;
        TimestampUser = timestampService?.User ?? "";
        TimestampCertificate = timestampService?.Certificate ?? "";
        Choice = choice ?? new SigningCertificateChoice(SigningCertificateSource.Own);
        OwnName = suggestedName ?? "";
        _shown = Choice.Source switch
        {
            SigningCertificateSource.Store when Choice.Fingerprint is { } fingerprint => SigningCertificates.FromStore(fingerprint),
            SigningCertificateSource.File => null,   // needs the password, which is not stored
            SigningCertificateSource.Token => null,  // shown once the card is read in the window
            _ => OwnToShow(OwnFileOf(Choice)),
        };
        // a file chosen before the list existed: on the list as well
        if (Choice is { Source: SigningCertificateSource.File, Path: { } chosen } && !Files.Any(f => SamePath(f.Path, chosen))) Files.Add(Choice);
        OptionsExpanded = UseTimestamps || UseLongTerm || UseArchive;
        RefreshRows();
        // to save room only the group of the chosen certificate is open; the own one also while there is none yet (to create one)
        OwnExpanded = Choice.Source == SigningCertificateSource.Own || Rows.Count == 0;
        FilesExpanded = Choice.Source == SigningCertificateSource.File;
        CardsExpanded = Choice.Source == SigningCertificateSource.Token;
        StoreExpanded = Choice.Source == SigningCertificateSource.Store;
        Target(null);   // the chosen own certificate (or the first): whether it has a password
    }

    // ---- The areas of the window, each foldable (decided on 11.11.2026)

    /// <summary>Whether the area „Zeitstempel und Langzeitprüfung“ is open: at first only if one of them is switched on.</summary>
    [ObservableProperty] public partial bool OptionsExpanded { get; set; }

    /// <summary>Whether the description of the kinds of certificates is open (closed at first).</summary>
    [ObservableProperty] public partial bool TypesExpanded { get; set; }

    /// <summary>Whether the group of the own certificates is open.</summary>
    [ObservableProperty] public partial bool OwnExpanded { get; set; }

    /// <summary>Whether the group of the certificate files is open.</summary>
    [ObservableProperty] public partial bool FilesExpanded { get; set; }

    /// <summary>Whether the group of the signature card is open.</summary>
    [ObservableProperty] public partial bool CardsExpanded { get; set; }

    /// <summary>Whether the group of the Windows store is open.</summary>
    [ObservableProperty] public partial bool StoreExpanded { get; set; }

    /// <summary>The certificate files on the list (path, fingerprint, what is shown — never a password); saved by the caller.</summary>
    public List<SigningCertificateChoice> Files { get; }

    /// <summary>All certificates, with exactly one default (docs/ABLAUFPLAN.md, 9.4.0, step 1); also offered when signing.</summary>
    public ObservableCollection<CertificateRow> Rows { get; } = [];

    /// <summary>The rows of the own certificates.</summary>
    public ObservableCollection<CertificateRow> OwnRows { get; } = [];

    /// <summary>The rows of the certificate files.</summary>
    public ObservableCollection<CertificateRow> FileRows { get; } = [];

    /// <summary>The rows of the signature card.</summary>
    public ObservableCollection<CertificateRow> CardRows { get; } = [];

    /// <summary>The rows of the Windows store.</summary>
    public ObservableCollection<CertificateRow> StoreRows { get; } = [];

    /// <summary>Headings of the groups with their number of certificates.</summary>
    public string OwnHeader => Count(Tr.T("Eigene Zertifikate"), OwnRows.Count);

    public string FilesHeader => Count(Tr.T("Zertifikatsdateien"), FileRows.Count);

    public string CardsHeader => Count(Tr.T("Signaturkarte oder Token"), CardRows.Count);

    public string StoreHeader => Count(Tr.T("Zertifikatsspeicher von Windows"), StoreRows.Count);

    private static string Count(string title, int n) => n == 0 ? title : $"{title} ({n})";

    private static bool SamePath(string? a, string? b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ---- Own certificates: the first one at the old place (settings from before keep working), further ones beside it

    /// <summary>The file of the first own certificate.</summary>
    private string DefaultOwn => _ownFile ?? SigningCertificates.OwnFile;

    /// <summary>The file an own choice stands for: its path, or the first one (a choice from before has none).</summary>
    private string OwnFileOf(SigningCertificateChoice choice) => choice.Path ?? DefaultOwn;

    /// <summary>The choice to store for an own certificate: without a path for the first one, as before.</summary>
    private SigningCertificateChoice ChoiceFor(string file) =>
        new(SigningCertificateSource.Own, Path: SamePath(file, DefaultOwn) ? null : file);

    /// <summary>Whether a row stands for the choice.</summary>
    private bool Matches(SigningCertificateChoice row, SigningCertificateChoice choice) => row.Source == choice.Source && row.Source switch
    {
        SigningCertificateSource.Own => SamePath(OwnFileOf(row), OwnFileOf(choice)),
        SigningCertificateSource.File => SamePath(row.Path, choice.Path),
        _ => row.Fingerprint is not null && row.Fingerprint == choice.Fingerprint,
    };

    /// <summary>Valid until and level as one line.</summary>
    private static string Details(DateTimeOffset? validUntil, Core.Pdf.Signatures.SignatureLevel? level) =>
        string.Join(" · ", new[]
        {
            validUntil is { } until ? Tr.T("gültig bis {0}", until.LocalDateTime.ToString("d", System.Globalization.CultureInfo.CurrentCulture)) : null,
            level is { } l ? SignatureTexts.Level(l) : null,
        }.Where(p => p is not null));

    /// <summary>Builds the lists anew: own certificates, files, card, Windows store; the chosen one is the default.</summary>
    private void RefreshRows()
    {
        var rows = new List<CertificateRow>();
        var now = DateTimeOffset.Now;
        CertificateRow Row(CertificateGroup group, SigningCertificate certificate, string origin, SigningCertificateChoice choice, object? item, bool locked = false)
        {
            var rating = SigningCertificates.Rate(certificate, now);
            return new CertificateRow(group, certificate.Name, origin, Details(new DateTimeOffset(certificate.ValidUntil), rating.Level), rating.Problem, rating.Hint,
                choice, item) { FirstOfGroup = rows.Count == 0 || rows[^1].Group != group, Level = rating.Level, Locked = locked,
                Export = group == CertificateGroup.Own ? AskExportCommand : null, Password = group == CertificateGroup.Own ? AskPasswordCommand : null,
                Remove = group == CertificateGroup.Own ? AskRemoveOwnCommand : null };
        }
        foreach (var file in SigningCertificates.OwnFiles(DefaultOwn))
        {
            var locked = SigningCertificates.OwnState(file) == OwnCertificateState.Locked;
            var origin = locked ? Tr.T("auf diesem Rechner erzeugt · mit eigenem Passwort geschützt") : Tr.T("auf diesem Rechner erzeugt");
            if (OwnToShow(file) is { } own)
                using (own)
                    rows.Add(Row(CertificateGroup.Own, own, origin, ChoiceFor(file), file, locked));
            else
                // unreadable (damaged, or from another account): listed so that it can be deleted
                rows.Add(new CertificateRow(CertificateGroup.Own, System.IO.Path.GetFileName(file), file, null, Tr.T("Die Datei lässt sich nicht lesen."), null,
                    ChoiceFor(file), file) { FirstOfGroup = rows.Count == 0 || rows[^1].Group != CertificateGroup.Own, Remove = AskRemoveOwnCommand });
        }
        foreach (var file in Files)
            rows.Add(new CertificateRow(CertificateGroup.File, file.Name ?? System.IO.Path.GetFileName(file.Path ?? ""), file.Path ?? "",
                Details(file.ValidUntil, file.Level) is { Length: > 0 } details ? details + " · " + Tr.T("Passwort beim Unterschreiben") : Tr.T("Passwort beim Unterschreiben"),
                file.Path is { } path && !System.IO.File.Exists(path) ? Tr.T("Die Datei ist nicht mehr da.") : null, null, file)
                { FirstOfGroup = rows.Count == 0 || rows[^1].Group != CertificateGroup.File, Forget = ForgetFileCommand, Level = file.Level });
        foreach (var card in CardCertificates)
        {
            using var described = SmartCards.Describe(card);
            rows.Add(Row(CertificateGroup.Card, described, card.Token, new SigningCertificateChoice(SigningCertificateSource.Token, Path: card.Module, Fingerprint: described.Fingerprint), card));
        }
        foreach (var stored in StoreAvailable ? StoreCertificates : [])
            rows.Add(Row(CertificateGroup.Store, stored, stored.Certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, forIssuer: true),
                new SigningCertificateChoice(SigningCertificateSource.Store, Fingerprint: stored.Fingerprint), stored));
        foreach (var row in rows) row.IsDefault = Matches(row.Choice, Choice);
        Fill(Rows, rows);
        Fill(OwnRows, rows.Where(r => r.Group == CertificateGroup.Own));
        Fill(FileRows, rows.Where(r => r.Group == CertificateGroup.File));
        Fill(CardRows, rows.Where(r => r.Group == CertificateGroup.Card));
        Fill(StoreRows, rows.Where(r => r.Group == CertificateGroup.Store));
        HasOwn = OwnRows.Count > 0;
        OnPropertyChanged(nameof(OwnHeader));
        OnPropertyChanged(nameof(FilesHeader));
        OnPropertyChanged(nameof(CardsHeader));
        OnPropertyChanged(nameof(StoreHeader));
        OnPropertyChanged(nameof(Incomplete));
        OnPropertyChanged(nameof(HasIncomplete));
        OnPropertyChanged(nameof(IncompleteHintShown));
    }

    private static void Fill(ObservableCollection<CertificateRow> target, IEnumerable<CertificateRow> rows)
    {
        target.Clear();
        foreach (var row in rows) target.Add(row);
    }

    /// <summary>Makes a row the default (its option button): an own certificate, a file (its password is asked when signing), a card or the store.</summary>
    [RelayCommand]
    private void Choose(CertificateRow? row)
    {
        if (row is null) return;
        if (!row.Selectable)
        {
            Report(row.Problem, error: true);
            RefreshRows();   // the option button back to the default
            return;
        }
        switch (row.Group)
        {
            case CertificateGroup.Own:
                UseOwn(row.OwnFile);
                break;
            case CertificateGroup.File:
                Show(null, row.Choice);
                Report(Tr.T("Das Passwort der Datei wird beim Unterschreiben abgefragt."), error: false);
                break;
            case CertificateGroup.Card when row.Item is TokenCertificate card:
                UseCard(card);
                break;
            case CertificateGroup.Store when row.Item is SigningCertificate stored:
                UseStore(stored);
                break;
        }
    }

    /// <summary>Takes a file off the list (the file itself stays); if it was the default, the first own certificate becomes it.</summary>
    [RelayCommand]
    private void ForgetFile(CertificateRow? row)
    {
        if (row is not { Group: CertificateGroup.File }) return;
        Files.RemoveAll(f => SamePath(f.Path, row.Choice.Path));
        if (Matches(row.Choice, Choice)) ChooseFirstOwn();
        RefreshRows();
    }

    /// <summary>The first own certificate as the default (or, without one, the one created at the first signature).</summary>
    private void ChooseFirstOwn()
    {
        var first = SigningCertificates.OwnFiles(DefaultOwn).FirstOrDefault();
        Show(first is null ? null : OwnToShow(first), first is null ? new SigningCertificateChoice(SigningCertificateSource.Own) : ChoiceFor(first));
    }

    /// <summary>
    /// After signing, fetch the revocation status of the certificates from their certification authorities and embed it (PAdES B-LT, KONZEPT.md,
    /// section 5.17, „Langzeitprüfung“); needs a connection, off unless switched on.
    /// </summary>
    [ObservableProperty] public partial bool UseLongTerm { get; set; }

    /// <summary>
    /// After signing (and the validation data), a document time stamp over the whole file (PAdES B-LTA, KONZEPT.md, section 5.17,
    /// „Archiv-Zeitstempel“); needs the time-stamping authority above.
    /// </summary>
    [ObservableProperty] public partial bool UseArchive { get; set; }

    // B-LTA needs B-LT (EN 319 142-1, 6.3): the archive time stamp switches the long-term validation on, switching that off takes the archive
    // time stamp with it (No. 527)
    partial void OnUseArchiveChanged(bool value)
    {
        if (value) UseLongTerm = true;
        OnPropertyChanged(nameof(OptionsSummary));
    }

    partial void OnUseLongTermChanged(bool value)
    {
        if (!value) UseArchive = false;
        OnPropertyChanged(nameof(OptionsSummary));
    }

    /// <summary>What is switched on, for the heading of the closed area.</summary>
    public string OptionsSummary => (UseTimestamps, UseLongTerm, UseArchive) switch
    {
        (false, false, _) => Tr.T("aus"),
        (true, false, _) => Tr.T("Zeitstempel an"),
        (false, true, _) => Tr.T("Langzeitprüfung an"),
        (true, true, false) => Tr.T("Zeitstempel und Langzeitprüfung an"),
        _ => Tr.T("Zeitstempel, Langzeitprüfung und Archiv-Zeitstempel an"),
    };

    // ---- Time stamps (PAdES B-T, KONZEPT.md, section 5.17, „Zeitstempel“): off unless switched on, no service preset

    /// <summary>Digital signatures get a time stamp from the service below (needs a connection).</summary>
    [ObservableProperty] public partial bool UseTimestamps { get; set; }

    /// <summary>Address of the time-stamping authority (http or https).</summary>
    [ObservableProperty] public partial string TimestampServer { get; set; }

    /// <summary>User name at the service (only services that want a login); the password is asked when signing, once per session.</summary>
    [ObservableProperty] public partial string TimestampUser { get; set; }

    /// <summary>Path of a customer certificate for the service (only services that want one); its password is asked when signing.</summary>
    [ObservableProperty] public partial string TimestampCertificate { get; set; }

    /// <summary>The service as entered in the window.</summary>
    public TimestampService Timestamp => new(TimestampServer.Trim(), Blank(TimestampUser), Blank(TimestampCertificate));

    private static string? Blank(string text) => text.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    /// <summary>Whether the address can be used.</summary>
    public bool TimestampServerValid => HttpTimestampAuthority.TryParse(TimestampServer, out _);

    /// <summary>What to say under the address (a wrong one, or a login over http); empty if all is well.</summary>
    public string TimestampHint => !UseTimestamps ? ""
        : !TimestampServerValid ? Tr.T("Bitte die Adresse eines Zeitstempeldienstes eingeben (beginnt mit http:// oder https://). Ohne gültige Adresse wird ohne Zeitstempel signiert.")
        : Timestamp.HasLogin && !TimestampServer.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? Tr.T("Mit Anmeldung nur über eine https-Adresse: über http wäre das Passwort unterwegs lesbar.")
            : "";

    partial void OnUseTimestampsChanged(bool value)
    {
        OnPropertyChanged(nameof(TimestampHint));
        OnPropertyChanged(nameof(OptionsSummary));
        Changed();
    }

    partial void OnTimestampServerChanged(string value)
    {
        OnPropertyChanged(nameof(TimestampServerValid));
        OnPropertyChanged(nameof(TimestampHint));
        Changed();
    }

    partial void OnTimestampUserChanged(string value)
    {
        OnPropertyChanged(nameof(TimestampHint));
        Changed();
    }

    partial void OnTimestampCertificateChanged(string value)
    {
        OnPropertyChanged(nameof(TimestampHint));
        Changed();
    }

    /// <summary>
    /// „Bekannte Dienste“: puts the address of a free service whose certificate the common systems trust into the field and switches time stamps
    /// on. Nothing is preset; the user chooses (KONZEPT.md, section 5.17, „Zeitstempel“).
    /// </summary>
    /// <param name="address">The address from the list.</param>
    [RelayCommand]
    private void UseTimestampServer(string address)
    {
        TimestampServer = address;
        UseTimestamps = true;
        Report(Tr.T("Adresse übernommen. „Dienst prüfen“ zeigt, ob er antwortet."), error: false);
    }

    /// <summary>
    /// Opens a connection to a service; null if it cannot be opened (the user cancelled a password, or the reason is reported). Set by the main
    /// window's model, which asks the passwords of a login once per session; by the tests, a service that needs no network.
    /// </summary>
    public Func<TimestampService, Task<ITimestampAuthority?>> OpenTimestampAuthority { get; set; } = service =>
        Task.FromResult<ITimestampAuthority?>(HttpTimestampAuthority.TryParse(service.Server, out var address) ? new HttpTimestampAuthority(address) : null);

    /// <summary>Whether a check of the service runs.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckTimestampServerCommand))]
    public partial bool CheckingTimestampServer { get; private set; }

    private bool CanCheckTimestampServer => !CheckingTimestampServer;

    /// <summary>„Dienst prüfen“: one request with a random value; says whether and who answered.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckTimestampServer))]
    private async Task CheckTimestampServer()
    {
        if (!HttpTimestampAuthority.TryParse(TimestampServer, out var address))
        {
            Report(Tr.T("Die Adresse ist ungültig (beginnt mit http:// oder https://)."), error: true);
            return;
        }
        CheckingTimestampServer = true;
        Report(Tr.T("Frage den Zeitstempeldienst …"), error: false);
        try
        {
            if (await OpenTimestampAuthority(Timestamp) is not { } authority)
            {
                Report(Tr.T("Nicht geprüft: die Anmeldung ließ sich nicht vorbereiten (Passwort nicht eingegeben oder Kundenzertifikat nicht lesbar)."), error: true);
                return;
            }
            var answer = await Task.Run(() => SignatureTimestamps.Probe(authority));
            Report(Tr.T("Der Dienst antwortet: {0}, Zeit {1}.", answer.Authority.Length > 0 ? answer.Authority : address.Host, answer.Time.LocalDateTime.ToString("g")) + " " +
                   (answer.Trusted ? Tr.T("Dieser Rechner vertraut ihm.") : Tr.T("Dieser Rechner kennt ihn nicht als vertrauenswürdig — PDF-Leser zeigen die Zeit dann womöglich als unbestätigt.")),
                error: false);
        }
        catch (TimestampException e)
        {
            Report(e.Message, error: true);
        }
        finally
        {
            CheckingTimestampServer = false;
        }
    }

    /// <summary>An own certificate to show: with its key if it has no password, otherwise without (it is shown, not used here).</summary>
    private static SigningCertificate? OwnToShow(string file) =>
        SigningCertificates.OwnState(file) == OwnCertificateState.Locked ? SigningCertificates.OwnPublic(file) : SigningCertificates.LoadOwn(file);

    /// <summary>An own certificate was created, removed or got another password (a password remembered for signing no longer applies).</summary>
    public bool OwnChanged { get; private set; }

    /// <summary>Whether the own certificate of the open panel (password, export) has a password of its own.</summary>
    [ObservableProperty] public partial bool HasPassword { get; private set; }

    /// <summary>The own certificate the panels work on, from the row clicked; without one the chosen own certificate or the first.</summary>
    private string? TargetFor(CertificateRow? row) =>
        row?.OwnFile ?? (Choice.Source == SigningCertificateSource.Own && System.IO.File.Exists(OwnFileOf(Choice)) ? OwnFileOf(Choice)
            : SigningCertificates.OwnFiles(DefaultOwn).FirstOrDefault());

    /// <summary>Sets the own certificate the panels work on.</summary>
    private bool Target(CertificateRow? row)
    {
        _target = TargetFor(row);
        HasPassword = _target is not null && SigningCertificates.OwnState(_target) == OwnCertificateState.Locked;
        OnPropertyChanged(nameof(TargetName));
        OnPropertyChanged(nameof(PasswordTitle));
        OnPropertyChanged(nameof(ExportTitle));
        OnPropertyChanged(nameof(RemoveQuestion));
        return _target is not null;
    }

    /// <summary>The name of the own certificate the open panel works on.</summary>
    public string TargetName => _target is { } file && OwnRows.FirstOrDefault(r => SamePath(r.OwnFile, file)) is { } row ? row.Name : "";

    /// <summary>Heading of the password panel.</summary>
    public string PasswordTitle => Tr.T("Passwort für „{0}“", TargetName);

    /// <summary>Heading of the export panel.</summary>
    public string ExportTitle => Tr.T("„{0}“ exportieren", TargetName);

    /// <summary>The question before deleting an own certificate.</summary>
    public string RemoveQuestion => Tr.T("„{0}“ samt seinem Schlüssel von diesem Rechner löschen? Schon gesetzte Unterschriften bleiben gültig.", TargetName);

    /// <summary>„Passwort …“ was clicked: the fields for setting, changing or removing it are shown.</summary>
    [ObservableProperty] public partial bool ChangingPassword { get; private set; }

    /// <summary>The current password (when it has one).</summary>
    [ObservableProperty] public partial string? PasswordCurrent { get; set; }

    /// <summary>The new password.</summary>
    [ObservableProperty] public partial string? PasswordNew { get; set; }

    /// <summary>The new password once more.</summary>
    [ObservableProperty] public partial string? PasswordRepeat { get; set; }

    partial void OnChangingPasswordChanged(bool value) => Changed();

    partial void OnPasswordNewChanged(string? value) => Changed();

    /// <summary>Shows the fields for the password of an own certificate (the row's, otherwise the chosen one).</summary>
    [RelayCommand]
    private void AskPassword(CertificateRow? row = null)
    {
        Exporting = false;
        ConfirmingRemove = false;
        ChangingPassword = Target(row);
        Report(null, error: false);
    }

    /// <summary>Closes the fields without changing anything.</summary>
    [RelayCommand]
    private void CancelPassword()
    {
        ChangingPassword = false;
        PasswordCurrent = PasswordNew = PasswordRepeat = null;
    }

    /// <summary>Sets or changes the password of the own certificate.</summary>
    [RelayCommand]
    private void SavePassword()
    {
        var next = PasswordNew ?? "";
        if (next.Length < SigningCertificates.MinOwnPassword)
        {
            Report(Tr.T("Das Passwort braucht mindestens {0} Zeichen.", SigningCertificates.MinOwnPassword), error: true);
            return;
        }
        if (next != PasswordRepeat)
        {
            Report(Tr.T("Die beiden Passwörter stimmen nicht überein."), error: true);
            return;
        }
        ApplyPassword(next, HasPassword ? Tr.T("Passwort geändert.") : Tr.T("Passwort festgelegt. Beim ersten Unterschreiben nach dem Start wird es abgefragt."));
    }

    /// <summary>Removes the password of the own certificate (the current one is needed).</summary>
    [RelayCommand]
    private void RemovePassword() => ApplyPassword(null, Tr.T("Passwort entfernt."));

    private void ApplyPassword(string? next, string done)
    {
        if (_target is null) return;
        try
        {
            if (!SigningCertificates.SetOwnPassword(HasPassword ? PasswordCurrent ?? "" : null, next, _target))
            {
                Report(Tr.T("Das Passwort ließ sich nicht speichern."), error: true);
                return;
            }
            HasPassword = next is not null;
            OwnChanged = true;
            CancelPassword();
            Report(done, error: false);
            RefreshRows();
        }
        catch (CertificatePasswordException)
        {
            Report(Tr.T("Das bisherige Passwort stimmt nicht."), error: true);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Report(Tr.T("Das Passwort ließ sich nicht speichern: {0}", e.Message), error: true);
        }
    }

    /// <summary>The choice to store in the settings.</summary>
    public SigningCertificateChoice Choice { get; private set; }

    /// <summary>Whether at least one own certificate exists.</summary>
    [ObservableProperty] public partial bool HasOwn { get; private set; }

    /// <summary>Name for a new own certificate.</summary>
    [ObservableProperty] public partial string OwnName { get; set; }

    /// <summary>Path of a chosen PKCS#12 file, before it is loaded.</summary>
    [ObservableProperty] public partial string? FilePath { get; set; }

    /// <summary>Password of the chosen file.</summary>
    [ObservableProperty] public partial string? FilePassword { get; set; }

    partial void OnFilePathChanged(string? value) => Changed();

    /// <summary>Error or confirmation after an action.</summary>
    [ObservableProperty] public partial string? Message { get; private set; }

    /// <summary>Whether the last action failed (the message is shown as an error).</summary>
    [ObservableProperty] public partial bool IsError { get; private set; }

    /// <summary>Whether the Windows store can be used.</summary>
    public static bool StoreAvailable => OperatingSystem.IsWindows();

    /// <summary>Certificates of the Windows store that can sign (read when first asked for).</summary>
    public IReadOnlyList<SigningCertificate> StoreCertificates => _store ??= NoStoreForTests ? [] : SigningCertificates.FromStore();

    /// <summary>Tests: the Windows store is not read (its certificates are the user's own; a picture of the window must not show them).</summary>
    [ThreadStatic] internal static bool NoStoreForTests;

    // ---- Signature cards (PKCS#11, KONZEPT.md, section 5.17, „Signaturkarte“): Linux; under Windows through the store

    /// <summary>Whether signature cards are reached through a driver here (not under Windows).</summary>
    public static bool CardsAvailable => SmartCards.Available;

    /// <summary>The card drivers found in the usual places, and one chosen by the user.</summary>
    public ObservableCollection<string> CardModules { get; } = [.. SmartCards.KnownModules()];

    /// <summary>The driver to read the cards with.</summary>
    [ObservableProperty] public partial string? CardModule { get; set; }

    /// <summary>The certificates on the cards, after „Karte lesen“.</summary>
    [ObservableProperty] public partial IReadOnlyList<TokenCertificate> CardCertificates { get; private set; } = [];

    partial void OnCardCertificatesChanged(IReadOnlyList<TokenCertificate> value) => RefreshRows();

    /// <summary>What the list says while it is empty: no driver (with the commands to install one), or not read yet.</summary>
    public string CardHint => CardModules.Count == 0
        ? Tr.T("Kein Kartentreiber gefunden. Debian/Ubuntu: sudo apt install opensc — Fedora: sudo dnf install opensc")
        : Tr.T("Karte einlegen, dann „Karte lesen“");

    /// <summary>A driver file the user chose (loading a driver runs its code: only one the user picked or from the system's places).</summary>
    /// <param name="path">The library file.</param>
    public void AddCardModule(string path)
    {
        if (!CardModules.Contains(path)) CardModules.Add(path);
        CardModule = path;
        OnPropertyChanged(nameof(CardHint));
    }

    /// <summary>„Karte lesen“: the certificates on the cards the driver reaches (no PIN needed).</summary>
    [RelayCommand]
    private async Task ReadCard()
    {
        if (CardModule is not { } module)
        {
            Report(Tr.T("Bitte zuerst einen Kartentreiber wählen."), error: true);
            return;
        }
        try
        {
            CardCertificates = await Task.Run(() => SmartCards.Certificates(module));
            Report(CardCertificates.Count == 0 ? await Task.Run(() => SmartCards.WhyNothing(module)) : null, error: CardCertificates.Count == 0);
        }
        catch (PdfKnead.Core.Platform.Pkcs11.Pkcs11Exception e)
        {
            Report(e.Message, error: true);
        }
    }

    /// <summary>Chooses a certificate of a card; the PIN is asked when signing.</summary>
    /// <param name="card">One of <see cref="CardCertificates"/>.</param>
    [RelayCommand]
    private void UseCard(TokenCertificate? card)
    {
        if (card is null) return;
        var shown = SmartCards.Describe(card);
        Show(shown, new SigningCertificateChoice(SigningCertificateSource.Token, Path: card.Module, Fingerprint: shown.Fingerprint));
        Report(card.PinPad ? Tr.T("Beim Unterschreiben wird die PIN am Kartenleser eingegeben.") : Tr.T("Beim Unterschreiben fragt PDF-Knead nach der PIN der Karte."), error: false);
    }

    /// <summary>Whether a certificate is known and can be shown.</summary>
    public bool HasCertificate => _shown is not null;

    /// <summary>Holder of the shown certificate.</summary>
    public string Name => _shown?.Name ?? Choice.Name ?? Choice.Source switch
    {
        SigningCertificateSource.File => System.IO.Path.GetFileName(Choice.Path ?? ""),
        SigningCertificateSource.Token => Tr.T("Zertifikat auf einer Signaturkarte"),
        _ => Tr.T("Noch kein Zertifikat"),
    };

    /// <summary>Where the shown certificate comes from.</summary>
    public string SourceText => Choice.Source switch
    {
        SigningCertificateSource.File => Tr.T("Aus einer Datei — das Passwort wird beim Signieren abgefragt"),
        SigningCertificateSource.Store => Tr.T("Aus dem Zertifikatsspeicher von Windows"),
        SigningCertificateSource.Token => Tr.T("Auf einer Signaturkarte — die PIN wird beim Signieren abgefragt"),
        _ => _shown is null ? Tr.T("Wird beim ersten Signieren erzeugt") : Tr.T("Eigenes Zertifikat, von PDF-Knead erzeugt"),
    };

    /// <summary>Validity of the shown certificate.</summary>
    public string ValidUntil => _shown is { } c ? Tr.T("Gültig bis {0}", c.ValidUntil.ToString("d", System.Globalization.CultureInfo.CurrentCulture)) : "";

    /// <summary>Why the shown certificate cannot sign now (expired, not yet valid, meant for something else); empty if it can (No. 526).</summary>
    public string Problem => _shown is { } c && SigningCertificates.Unusable(c.Certificate, DateTimeOffset.Now) is { } why
        ? Tr.T("Mit diesem Zertifikat lässt sich nicht unterschreiben: {0}", why) : "";

    /// <summary>Whether the shown certificate cannot sign now.</summary>
    public bool HasProblem => Problem.Length > 0;

    /// <summary>Fingerprint of the shown certificate.</summary>
    public string Fingerprint => _shown?.Fingerprint ?? "";

    /// <summary>What PDF readers will show about the identity.</summary>
    public string TrustHint => _shown is { SelfSigned: false }
        ? Tr.T("Von einer Zertifizierungsstelle ausgestellt: PDF-Leser, die ihr vertrauen, zeigen die Identität als bestätigt.")
        : Tr.T("Selbst ausgestellt: PDF-Leser zeigen „Dokument unverändert“, aber „Identität nicht bestätigt“, weil niemand Unabhängiges den Namen geprüft hat. Wer den Fingerabdruck von Ihnen kennt, kann ihn vergleichen.");

    /// <summary>The level of signature the shown certificate gives, with what it means (no legal advice).</summary>
    public string LevelHint => _shown is { } shown && SigningCertificates.Rate(shown, DateTimeOffset.Now) is var rating
        ? Tr.T("Stufe: {0}. {1}", SignatureTexts.Level(rating.Level), SignatureTexts.LevelMeaning(rating.Level, CertificateProfiles.Of(shown.Certificate).Kind))
          + (rating.Hint is { } hint ? " " + hint : "")
        : "";

    /// <summary>Creates one more own certificate with the name (the others stay) and chooses it.</summary>
    [RelayCommand]
    private void CreateOwn()
    {
        try
        {
            var file = SigningCertificates.NextOwnFile(DefaultOwn);
            Show(SigningCertificates.CreateOwn(OwnName, file), ChoiceFor(file));
            OwnChanged = true;
            OwnExpanded = true;
            Report(Tr.T("Eigenes Zertifikat erzeugt."), error: false);
        }
        catch (ArgumentException)
        {
            Report(Tr.T("Bitte einen Namen eingeben."), error: true);
        }
    }

    /// <summary>Chooses an own certificate (the given file; without one the chosen or the first).</summary>
    [RelayCommand]
    private void UseOwn(string? file = null)
    {
        file ??= TargetFor(null);
        if (file is not null && OwnToShow(file) is { } own)
        {
            Show(own, ChoiceFor(file));
            Report(null, error: false);
        }
        else
        {
            Report(Tr.T("Es gibt noch kein eigenes Zertifikat."), error: true);
        }
    }

    /// <summary>The bin of a row was clicked: the question whether to delete that own certificate is shown.</summary>
    [ObservableProperty] public partial bool ConfirmingRemove { get; private set; }

    /// <summary>Asks whether to delete an own certificate (the row's, otherwise the chosen one); the answer comes from <see cref="RemoveOwnCommand"/>.</summary>
    [RelayCommand]
    private void AskRemoveOwn(CertificateRow? row = null)
    {
        CancelExport();
        CancelPassword();
        ConfirmingRemove = Target(row);
        Report(null, error: false);
    }

    /// <summary>Keeps the own certificate.</summary>
    [RelayCommand]
    private void KeepOwn() => ConfirmingRemove = false;

    /// <summary>Deletes the own certificate of the question from this computer; if it was the chosen one, the first remaining one becomes it.</summary>
    [RelayCommand]
    private void RemoveOwn()
    {
        ConfirmingRemove = false;
        if (_target is not { } file) return;
        var chosen = Choice.Source == SigningCertificateSource.Own && SamePath(OwnFileOf(Choice), file);
        if (chosen && _shown is { } own)
        {
            own.Dispose();
            _shown = null;
        }
        var removed = SigningCertificates.RemoveOwn(file);
        var gone = !System.IO.File.Exists(file);
        _target = null;
        OwnChanged = true;
        if (chosen) ChooseFirstOwn();
        else RefreshRows();
        Report(removed && gone ? Tr.T("Eigenes Zertifikat entfernt.") : Tr.T("Das Zertifikat ließ sich nicht ganz entfernen."), error: !removed || !gone);
    }

    /// <summary>„Exportieren …“ was clicked: the fields for the password of the file are shown.</summary>
    [ObservableProperty] public partial bool Exporting { get; private set; }

    /// <summary>Password of the exported file.</summary>
    [ObservableProperty] public partial string? ExportPassword { get; set; }

    /// <summary>The password once more.</summary>
    [ObservableProperty] public partial string? ExportRepeat { get; set; }

    partial void OnExportingChanged(bool value) => Changed();

    partial void OnExportPasswordChanged(string? value) => Changed();

    /// <summary>Lets the user choose where to save the exported file (set by the window); returns the path, or null.</summary>
    public Func<Task<string?>>? PickExportFile { get; set; }

    /// <summary>Shows the fields for exporting an own certificate (the row's, otherwise the chosen one).</summary>
    [RelayCommand]
    private void AskExport(CertificateRow? row = null)
    {
        ConfirmingRemove = false;
        ChangingPassword = false;
        Exporting = Target(row);
        Report(null, error: false);
    }

    /// <summary>Closes the fields without exporting.</summary>
    [RelayCommand]
    private void CancelExport()
    {
        Exporting = false;
        ExportPassword = ExportRepeat = ExportCurrent = null;
    }

    /// <summary>The password of the own certificate, needed to export it when it has one.</summary>
    [ObservableProperty] public partial string? ExportCurrent { get; set; }

    /// <summary>Checks the password, asks for the target and writes the own certificate as a file protected by it.</summary>
    [RelayCommand]
    private async Task ExportOwn()
    {
        var password = ExportPassword ?? "";
        if (password.Length < SigningCertificates.MinExportPassword)
        {
            Report(Tr.T("Das Passwort braucht mindestens {0} Zeichen.", SigningCertificates.MinExportPassword), error: true);
            return;
        }
        if (password != ExportRepeat)
        {
            Report(Tr.T("Die beiden Passwörter stimmen nicht überein."), error: true);
            return;
        }
        if (PickExportFile is null || await PickExportFile() is not { } target) return;
        try
        {
            if (_target is null || !SigningCertificates.ExportOwn(target, password, _target, HasPassword ? ExportCurrent ?? "" : null))
            {
                Report(Tr.T("Es gibt noch kein eigenes Zertifikat."), error: true);
                return;
            }
            CancelExport();
            Report(Tr.T("Zertifikat exportiert: {0}", Path.GetFileName(target)), error: false);
        }
        catch (CertificatePasswordException)
        {
            Report(Tr.T("Das Passwort des Zertifikats stimmt nicht."), error: true);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Report(Tr.T("Das Zertifikat ließ sich nicht exportieren: {0}", e.Message), error: true);
        }
    }

    /// <summary>Loads the chosen PKCS#12 file with the entered password, puts it on the list (one more, the others stay) and chooses it.</summary>
    [RelayCommand]
    private void LoadFile()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            Report(Tr.T("Bitte zuerst eine Datei wählen."), error: true);
            return;
        }
        try
        {
            var loaded = SigningCertificates.LoadFile(FilePath, FilePassword);
            // remembered with what the list shows (holder, valid until, level), never with the password
            var remembered = new SigningCertificateChoice(SigningCertificateSource.File, FilePath, loaded.Fingerprint, loaded.Name,
                new DateTimeOffset(loaded.ValidUntil), SigningCertificates.Rate(loaded, DateTimeOffset.Now).Level);
            Files.RemoveAll(f => SamePath(f.Path, FilePath));
            Files.Add(remembered);
            FilePath = null;
            FilePassword = null;
            FilesExpanded = true;
            Show(loaded, remembered);
            Report(Tr.T("Zertifikat aus der Datei übernommen."), error: false);
        }
        catch (CryptographicException)
        {
            Report(Tr.T("Die Datei lässt sich nicht öffnen: falsches Passwort, oder sie enthält kein Zertifikat mit privatem Schlüssel."), error: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Report(Tr.T("Die Datei lässt sich nicht lesen."), error: true);
        }
    }

    /// <summary>Chooses a certificate of the Windows store.</summary>
    /// <param name="certificate">One of <see cref="StoreCertificates"/>.</param>
    [RelayCommand]
    private void UseStore(SigningCertificate? certificate)
    {
        if (certificate is null) return;
        Show(certificate, new SigningCertificateChoice(SigningCertificateSource.Store, Fingerprint: certificate.Fingerprint));
        Report(null, error: false);
    }

    /// <summary>Shows a certificate as the chosen one (null = none yet); the one shown before is released unless it belongs to the store list.</summary>
    private void Show(SigningCertificate? certificate, SigningCertificateChoice choice)
    {
        if (_shown is { } old && !ReferenceEquals(old, certificate) && !(_store?.Contains(old) ?? false)) old.Dispose();
        _shown = certificate;
        Choice = choice;
        OnPropertyChanged(nameof(HasCertificate));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(ValidUntil));
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(Fingerprint));
        OnPropertyChanged(nameof(TrustHint));
        OnPropertyChanged(nameof(LevelHint));
        RefreshRows();
    }

    private void Report(string? message, bool error)
    {
        Message = message;
        IsError = error;
    }

    // ---- Closing with something half done (decided on 11.11.2026): first say what it is, then close if wanted

    /// <summary>What is not complete: what would be lost or not work as expected on closing now; empty if nothing.</summary>
    public IReadOnlyList<string> Incomplete
    {
        get
        {
            var list = new List<string>();
            if (UseTimestamps && TimestampHint.Length > 0) list.Add(Tr.T("Zeitstempel: {0}", TimestampHint));
            if (!string.IsNullOrWhiteSpace(FilePath))
                list.Add(Tr.T("Eine Zertifikatsdatei ist gewählt, aber noch nicht hinzugefügt („Hinzufügen“ mit ihrem Passwort)."));
            if (ChangingPassword && !string.IsNullOrEmpty(PasswordNew))
                list.Add(Tr.T("Das neue Passwort des eigenen Zertifikats ist noch nicht festgelegt."));
            if (Exporting && !string.IsNullOrEmpty(ExportPassword))
                list.Add(Tr.T("Der Export des eigenen Zertifikats ist nicht abgeschlossen."));
            if (Rows.FirstOrDefault(r => r.IsDefault) is { HasProblem: true } chosen)
                list.Add(Tr.T("Mit dem gewählten Zertifikat lässt sich nicht unterschreiben: {0}", chosen.Problem!));
            else if (HasProblem)
                list.Add(Problem);
            return list;
        }
    }

    /// <summary>Whether something is not complete.</summary>
    public bool HasIncomplete => Incomplete.Count > 0;

    /// <summary>Whether the warning about it is shown (after „Schließen“ was clicked once).</summary>
    [ObservableProperty] public partial bool WarningShown { get; private set; }

    /// <summary>Whether the short hint beside „Schließen“ is shown: something is not complete and the warning is not open.</summary>
    public bool IncompleteHintShown => HasIncomplete && !WarningShown;

    partial void OnWarningShownChanged(bool value) => OnPropertyChanged(nameof(IncompleteHintShown));

    private void Changed()
    {
        OnPropertyChanged(nameof(Incomplete));
        OnPropertyChanged(nameof(HasIncomplete));
        OnPropertyChanged(nameof(IncompleteHintShown));
        if (WarningShown && !HasIncomplete) WarningShown = false;
    }

    /// <summary>„Schließen“: true if the window may close; with something not complete the warning is shown first and false returned.</summary>
    public bool TryClose()
    {
        Changed();
        if (!HasIncomplete || WarningShown) return true;
        WarningShown = true;
        return false;
    }

    /// <summary>„Zurück“ in the warning: stays in the window to complete it.</summary>
    [RelayCommand]
    private void Back() => WarningShown = false;

    /// <summary>Releases the private keys of the shown and listed certificates.</summary>
    public void Dispose()
    {
        if (_shown is { } shown && !(_store?.Contains(shown) ?? false)) shown.Dispose();
        foreach (var certificate in _store ?? []) certificate.Dispose();
    }
}
