using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Platform;

namespace PdfKnead.ViewModels;

/// <summary>A font family to choose from (German label, shown translated).</summary>
public sealed record FontOption(ElementFont Font, string Label);

/// <summary>A text colour to choose from.</summary>
public sealed record ColorOption(int Rgb, string Label)
{
    public IBrush Brush { get; } = new SolidColorBrush(Color.FromRgb((byte)(Rgb >> 16), (byte)(Rgb >> 8), (byte)Rgb));
}

/// <summary>Defaults for new elements; the settings in the „Einfügen“ tab change them.</summary>
public static class ElementDefaults
{
    public static TextElement Text { get; set; } = new() { Rect = default };

    /// <summary>The signature chosen last („Unterschrift“ tool).</summary>
    public static PageElement? Signature { get; set; }

    /// <summary>The image chosen last („Bild“ tool).</summary>
    public static ImageElement? Image { get; set; }

    /// <summary>Appearance of the next shape (the properties of the shape changed last apply to the next one, as with fields).</summary>
    public static ShapeElement Shape { get; set; } = new() { Rect = default };

    /// <summary>Kind and settings of the next code (those of the code changed last); its content starts empty.</summary>
    public static CodeElement Code { get; set; } = new() { Rect = default };
}

/// <summary>Inserting and formatting elements, „Einfügen“ tab.</summary>
public sealed partial class MainViewModel
{
    public static IReadOnlyList<FontOption> FontOptions { get; } =
    [
        new(ElementFont.Sans, "Ohne Serifen"),
        new(ElementFont.Serif, "Mit Serifen"),
        new(ElementFont.Mono, "Feste Breite"),
    ];

    public static IReadOnlyList<double> FontSizes { get; } = [6, 7, 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 60, 72];

    public static IReadOnlyList<ColorOption> ColorOptions { get; } =
    [
        new(0x000000, "Schwarz"),
        new(0x505050, "Grau"),
        new(0x1A4FA0, "Blau"),
        new(0xC00000, "Rot"),
        new(0x1E7B34, "Grün"),
    ];

    private bool _syncingFormat;

    [ObservableProperty] public partial FontOption TextFont { get; set; } = FontOptions[0];

    [ObservableProperty] public partial double TextSize { get; set; } = 12;

    [ObservableProperty] public partial ColorOption TextColor { get; set; } = ColorOptions[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlignLeft), nameof(AlignCenter), nameof(AlignRight))]
    public partial ElementAlignment TextAlignment { get; set; } = ElementAlignment.Left;

    public bool AlignLeft => TextAlignment == ElementAlignment.Left;

    public bool AlignCenter => TextAlignment == ElementAlignment.Center;

    public bool AlignRight => TextAlignment == ElementAlignment.Right;

    /// <summary>Whether the selected element is a fillable form field (can be toggled).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFieldSelected))]
    public partial bool TextIsField { get; set; }

    /// <summary>Whether long lines wrap (otherwise lines break only at line breaks).</summary>
    [ObservableProperty] public partial bool TextMultiline { get; set; } = true;

    public bool HasFieldSelected => HasTextElementSelected && TextIsField;

    /// <summary>Font size as text, so that custom values can be typed (comma or point; 1 … 500).</summary>
    public string TextSizeText
    {
        get => TextSize.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
        set
        {
            if (double.TryParse(value?.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var size) && size is >= 1 and <= 500)
                TextSize = Math.Round(size, 1);
            OnPropertyChanged();
        }
    }

    /// <summary>Name of the selected form field (empty: assigned when saving).</summary>
    public string FieldName
    {
        get => SelectedDocument?.SelectedElement is { Element: TextElement t } ? t.FieldName : "";
        set
        {
            var name = (value ?? "").Trim();
            if (SelectedDocument is { SelectedElement: { Element: TextElement { IsField: true } t } selected } doc && name != t.FieldName)
                doc.ReplaceElement(selected, t with { FieldName = name }, Tr.T("Feldname ändern"));
            OnPropertyChanged();
        }
    }

    partial void OnTextMultilineChanged(bool value) => ApplyFormat(t => t with { Multiline = value }, Tr.T("Zeilenumbruch ändern"));

    public bool HasElementSelected => SelectedDocument?.SelectedElement is not null;

    public bool HasTextElementSelected => SelectedDocument?.SelectedElement is { Element: TextElement };

    /// <summary>
    /// Whether the „Text“ panel on the right is visible: a text element is selected, or the „Text“ tool is on (then the panel sets
    /// the format of the next text). Not in form design or while existing text is edited, which have their own panels, and not
    /// while the panel for shapes and images is showing.
    /// </summary>
    public bool ShowTextProperties => SelectedDocument is { IsFieldDesign: false, RichEditor: null } doc && !ElementProperties.IsVisible && !CodeProperties.IsVisible
        && (doc.SelectedElement is { Element: TextElement } || Tool == ViewTool.InsertText);

    partial void OnTextFontChanged(FontOption value) => ApplyFormat(t => t with { Font = value.Font }, Tr.T("Schrift ändern"));

    partial void OnTextSizeChanged(double value)
    {
        OnPropertyChanged(nameof(TextSizeText));
        if (value is < 1 or > 500 || double.IsNaN(value)) return;
        ApplyFormat(t => t with { FontSize = value }, Tr.T("Schriftgröße ändern"));
    }

    partial void OnTextColorChanged(ColorOption value) => ApplyFormat(t => t with { Color = value.Rgb }, Tr.T("Textfarbe ändern"));

    partial void OnTextAlignmentChanged(ElementAlignment value) => ApplyFormat(t => t with { Alignment = value }, Tr.T("Ausrichtung ändern"));

    partial void OnTextIsFieldChanged(bool value)
    {
        if (_syncingFormat || SelectedDocument is not { SelectedElement: { Element: TextElement t } selected } doc) return;
        var changed = t with { IsField = value };
        // a text becomes a form field or a field a text: the rights of both (a field needs bits 4 and 6, Table 22)
        if (!doc.CanChange(t) || !doc.CanChange(changed))
        {
            Message = Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen.");
            SyncFormatFromSelection();
            return;
        }
        doc.ReplaceElement(selected, changed, value ? Tr.T("In Textfeld umwandeln") : Tr.T("In festen Text umwandeln"));
    }

    [RelayCommand]
    private void SetAlignment(ElementAlignment alignment) => TextAlignment = alignment;

    /// <summary>Changes the default for new elements and the selected text element as well (one step).</summary>
    private void ApplyFormat(Func<TextElement, TextElement> change, string description)
    {
        if (_syncingFormat) return;
        ElementDefaults.Text = change(ElementDefaults.Text);
        if (SelectedDocument is { SelectedElement: { Element: TextElement t } selected } doc)
            doc.ReplaceElement(selected, ElementAppearance.FitHeight(change(t)), description);
    }

    /// <summary>Selection changed: the settings show the selected element.</summary>
    private void SyncFormatFromSelection()
    {
        OnPropertyChanged(nameof(HasElementSelected));
        OnPropertyChanged(nameof(HasTextElementSelected));
        OnPropertyChanged(nameof(HasFieldSelected));
        OnPropertyChanged(nameof(FieldName));
        OnPropertyChanged(nameof(ShowTextProperties));
        DeleteElementCommand.NotifyCanExecuteChanged();
        _syncingFormat = true;
        try
        {
            // Nothing (or no text) selected: show the default the next text will be created with, not the format of
            // the element selected last (which would look like the default without being it)
            var t = SelectedDocument?.SelectedElement is { Element: TextElement selected } ? selected : ElementDefaults.Text with { IsField = false };
            TextFont = FontOptions.FirstOrDefault(f => f.Font == t.Font) ?? FontOptions[0];
            TextSize = t.FontSize;
            TextColor = ColorOptions.FirstOrDefault(c => c.Rgb == t.Color) ?? new ColorOption(t.Color, "Eigene Farbe");
            TextAlignment = t.Alignment;
            TextIsField = t.IsField;
            TextMultiline = t.Multiline;
        }
        finally
        {
            _syncingFormat = false;
        }
    }

    /// <summary>Whether the selection can be deleted; form fields only in „Formular bearbeiten“ mode, so that nothing is deleted by accident while filling in.</summary>
    public bool CanDeleteElement => SelectedDocument?.SelectedElement is { } selected && (SelectedDocument.FieldsMovable || !DocumentViewModel.IsFormField(selected.Element))
        && SelectedDocument.CanChange(selected.Element);

    [RelayCommand(CanExecute = nameof(CanDeleteElement))]
    private void DeleteElement()
    {
        if (!EnsureAllowed("insert.element")) return;
        if (CanDeleteElement) SelectedDocument?.DeleteSelectedElement();
    }

    // ------------------------------------------------------------ Signature

    /// <summary>Lets the user choose a signature (set by the view): takes the preset and returns the choice, or null.</summary>
    public Func<PageElement, Task<PageElement?>>? PickSignature { get; set; }

    /// <summary>Shows the „Zertifikat“ window (set by the view); returns when it is closed.</summary>
    public Func<CertificateViewModel, Task>? ShowCertificate { get; set; }

    /// <summary>„Zertifikat“ in the Signature tab: shows and changes the certificate for digital signatures; the choice is stored.</summary>
    [RelayCommand]
    private async Task ManageCertificate()
    {
        if (!EnsureAllowed("signature.certificate")) return;
        if (ShowCertificate is not { } show) return;
        var settings = Services.UserSettings.Current;
        using var model = new CertificateViewModel(settings.SigningCertificate, settings.SignatureName, OwnCertificateFile, settings.SignatureTimestamps,
            TimestampServiceOf(settings), settings.SignatureLongTerm, settings.SignatureArchive, settings.Files)
        {
            OpenTimestampAuthority = service => OpenTimestampAuthorityAsync(service),
        };
        await show(model);
        var service = model.Timestamp;
        Services.UserSettings.Update(s => s with
        {
            SigningCertificate = model.Choice,
            CertificateFiles = model.Files,
            SignatureTimestamps = model.UseTimestamps,
            TimestampServer = service.Server.Length > 0 ? service.Server : null,
            TimestampUser = service.User,
            TimestampCertificate = service.Certificate,
            SignatureLongTerm = model.UseLongTerm,
            SignatureArchive = model.UseArchive,
        });
        // a new password, a new or removed certificate: the one remembered no longer applies
        if (model.OwnChanged) _ownPasswords.Clear();
        if (service != TimestampServiceOf(settings)) ForgetTimestampLogin();
    }

    /// <summary>Shows the window of the first start (set by the view): true for „Los geht’s“, false if it was closed otherwise.</summary>
    public Func<WelcomeViewModel, Task<bool>>? ShowWelcome { get; set; }

    /// <summary>
    /// The first start in one window (decided on 24.11.2026): the language first — preselected as the system's where a translation exists,
    /// otherwise English, switched at once —, then the name for the own certificate for digital signatures (which then also stands under the
    /// signatures; not asked if one exists) and whether PDF-Knead should check for updates (off at first, KONZEPT §5.26; not asked when the
    /// IT switched updates off). Shown while the certificate or the updates have not been offered yet; the choices go into the settings —
    /// closing the window counts as an answer too: the language stays as chosen, nothing is created, updates stay off.
    /// </summary>
    /// <param name="ownFile">File of the own certificate; null = the default (tests supply their own).</param>
    /// <returns>A task that completes when the first start is done.</returns>
    public async Task OfferWelcomeAsync(string? ownFile = null)
    {
        var settings = Services.UserSettings.Current;
        if ((settings.CertificateOffered && settings.UpdatesAsked) || ShowWelcome is not { } show) return;
        var exists = await Task.Run(() => SigningCertificates.OwnState(ownFile) != OwnCertificateState.Missing);
        var askName = !settings.CertificateOffered && !exists;
        var askUpdates = !settings.UpdatesAsked && !Services.Updates.OffByPolicy;
        var model = new WelcomeViewModel(Core.Localization.Languages.All(out _), settings.SignatureName, askName, askUpdates);
        var started = await show(model);
        Services.UserSettings.Update(s => s with { Language = Core.Localization.Tr.Current.Code });
        if (started && askName && model.Name.Trim() is { Length: > 0 } name)
        {
            // RSA key generation takes a moment: in the background
            using var created = await Task.Run(() => SigningCertificates.CreateOwn(name, ownFile));
            Services.UserSettings.Update(s => s with { SignatureName = name, SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.Own) });
            ShowNotice(Tr.T("Eigenes Zertifikat für „{0}“ angelegt.", created.Name));
        }
        // offered once each, whatever the answer
        Services.UserSettings.Update(s => s with
        {
            CertificateOffered = true,
            UpdatesAsked = s.UpdatesAsked || askUpdates,
            UpdateCheck = askUpdates ? started && model.UpdatesOn : s.UpdateCheck,
        });
    }

    /// <summary>Shows the „Signaturen“ window (set by the view); returns when it is closed.</summary>
    public Func<SignaturesViewModel, Task>? ShowSignatures { get; set; }

    /// <summary>
    /// „Signaturen“ in the Signature tab, or a click on the mark in a document's tab: the digital signatures of that document and their
    /// check.
    /// </summary>
    /// <param name="target">The document whose mark was clicked (it becomes the current one); null = the current document.</param>
    [RelayCommand]
    private async Task ShowSignatureDetails(DocumentViewModel? target)
    {
        if (target is not null) SelectedDocument = target;
        if (SelectedDocument is not { } doc || ShowSignatures is not { } show) return;
        SignaturesViewModel? model = null;
        model = new SignaturesViewModel(doc.FileName, doc.SignatureChecks, doc.CheckingSignatures, page =>
        {
            if (doc.PositionOf(page) is { } position) doc.ScrollToPage(position);
        })
        {
            OpenTrustLists = async () =>
            {
                if (ShowTrustList is not { } open) return;
                var lists = new TrustListViewModel();
                await open(lists);
                if (!lists.Changed) return;
                // loaded or removed: every open document is checked again, the window shows the new result
                await Task.WhenAll(Documents.Select(d => d.CheckSignaturesAsync()));
                model!.Update(doc.SignatureChecks);
            },
            AddArchive = async () =>
            {
                if (await AddArchiveTimestampAsync(doc)) model!.Update(doc.SignatureChecks);
            },
            SaveReport = () => SaveValidationReportAsync(doc, model!.Rows.Select(r => r.Check).ToList()),
        };
        await show(model);
    }

    /// <summary>
    /// „Archiv-Zeitstempel hinzufügen“ (KONZEPT.md, section 5.17, „Archiv-Zeitstempel“): appends to the signed file the validation data of all
    /// its signatures as they are now and a document time stamp (PAdES B-LTA) — the earlier signatures stay valid. Renews the protection before
    /// the certificates of the earlier time stamp expire.
    /// </summary>
    /// <param name="doc">The document; it must be saved and signed.</param>
    /// <returns>Whether the file was extended.</returns>
    internal async Task<bool> AddArchiveTimestampAsync(DocumentViewModel doc)
    {
        if (doc.IsUntitled || doc.IsModified)
        {
            Message = Tr.T("Bitte das Dokument zuerst speichern.");
            return false;
        }
        if (DigitalSignatures.Find(doc.Document).Count == 0)
        {
            Message = Tr.T("Das Dokument hat keine digitale Signatur, die ein Archiv-Zeitstempel schützen könnte.");
            return false;
        }
        // only after a successful check (TR-ESOR, Annex M.3, 4.7; decision D, docs/ABLAUFPLAN.md, 9.3.4): a stamp proves the state of today, so
        // a file with a broken signature would only look renewed (docs/FEHLERGEDAECHTNIS.md, No. 713); the check of the document is awaited
        if (doc.CheckingSignatures || doc.SignatureChecks.Count == 0) await doc.CheckSignaturesAsync();
        if (doc.SignatureChecks.Where(c => !SignatureTexts.IsValid(c)).Select(c => c.FieldName is { Length: > 0 } field ? field : c.SignerName ?? "?").ToList()
            is { Count: > 0 } broken)
        {
            Message = Tr.T("Kein Archiv-Zeitstempel: er belegt nur den heutigen Zustand, und die Datei enthält Signaturen, die nicht gültig sind ({0}).",
                string.Join(", ", broken));
            return false;
        }
        ITimestampAuthority? timestamps;
        if (Timestamps is { } test) timestamps = test();
        else
        {
            var settings = Services.UserSettings.Current;
            if (TimestampServiceOf(settings) is not { } service || !HttpTimestampAuthority.TryParse(service.Server, out _))
            {
                Message = Tr.T("Für einen Archiv-Zeitstempel bitte unter „Zertifikat“ einen Zeitstempeldienst eintragen.");
                return false;
            }
            timestamps = await OpenTimestampAuthorityAsync(service);
        }
        if (timestamps is null) return false;
        Busy = Tr.T("Hole Sperrstatus und Archiv-Zeitstempel …");
        try
        {
            var result = await doc.AddArchiveTimestampAsync(timestamps, Revocation() ?? ArchiveRevocation());
            Message = result.LongTermMissing.Count > 0
                ? Tr.T("Archiv-Zeitstempel hinzugefügt, aber ohne vollständige Langzeitprüfung: für {0} war kein Sperrstatus zu bekommen.", string.Join(", ", result.LongTermMissing))
                : "";
            // qualified signatures, a service that is not (TR-ESOR, 2.1): said once the file is checked again, no refusal
            if (string.IsNullOrEmpty(Message))
            {
                await doc.CheckSignaturesAsync();
                Message = SignatureTexts.ArchiveServiceHint(doc.SignatureChecks) ?? "";
            }
            ShowNotice(Tr.T("Archiv-Zeitstempel hinzugefügt: {0}", doc.FileName));
            return true;
        }
        catch (TimestampException e)
        {
            if (e is TimestampLoginException) ForgetTimestampLogin();
            Message = e.Message;
        }
        catch (CertificateRevokedException e)
        {
            Message = Tr.T("Kein Archiv-Zeitstempel: {0}", e.Message);
        }
        catch (Exception e) when (Services.Failure.Handled(e))
        {
            Message = Tr.T("Archiv-Zeitstempel fehlgeschlagen: {0}", Services.Failure.Text(e));
        }
        finally
        {
            Busy = null;
        }
        return false;
    }

    /// <summary>
    /// „Prüfbericht speichern …“ in the window „Signaturen“ (docs/ABLAUFPLAN.md, 9.3.5, step 4): the results as shown, as an HTML page or, with
    /// the extension .txt, as text — for the record; written on this computer only.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="checks">The results shown in the window.</param>
    internal async Task SaveValidationReportAsync(DocumentViewModel doc, IReadOnlyList<SignatureCheck> checks)
    {
        if (checks.Count == 0 || PickReportSavePath is null) return;
        var name = System.IO.Path.GetFileNameWithoutExtension(doc.FileName) + " – " + Tr.T("Prüfbericht") + ".html";
        if (await PickReportSavePath(Tr.T("Prüfbericht speichern …"), name, SaveFolder(doc)) is not { } path) return;
        try
        {
            var text = string.Equals(System.IO.Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase)
                ? ValidationReport.ToText(checks, doc.FileName, AppVersion.Display, DateTimeOffset.Now)
                : ValidationReport.ToHtml(checks, doc.FileName, AppVersion.Display, DateTimeOffset.Now);
            var bytes = new System.Text.UTF8Encoding(false).GetBytes(text);
            await Task.Run(() => Core.Pdf.Writing.PdfFileSaver.SaveFile(path, output => output.Write(bytes)));
            ShowNotice(Tr.T("Prüfbericht gespeichert: {0}", System.IO.Path.GetFileName(path)));
        }
        catch (Exception ex) when (Services.Failure.Handled(ex))
        {
            Message = Tr.T("Der Bericht lässt sich nicht speichern: {0}", Services.Failure.Text(ex));
        }
    }

    /// <summary>Shows the window „EU-Vertrauensliste“ (set by the view).</summary>
    public Func<TrustListViewModel, Task>? ShowTrustList { get; set; }

    /// <summary>
    /// The certificate for signing on saving, as chosen in „Zertifikat“: the own one (created on first use with the name of the
    /// signature; without a name the window opens), a file (its password is asked every time) or one from the system store.
    /// </summary>
    /// <returns>The certificate (owned by the caller), or null if there is none or the user cancelled; then the reason is shown.</returns>
    /// <summary>How often the password of a certificate file is asked before giving up with the reason.</summary>
    internal const int MaxPasswordAttempts = 3;

    /// <summary>
    /// The certificates that can sign now, as the window „Zertifikat“ lists them (own, files, Windows store; a card once it was read there), for
    /// „Unterschreiben mit … [ändern]“ in the signature window (docs/ABLAUFPLAN.md, 9.4.0, step 2).
    /// </summary>
    internal IReadOnlyList<CertificateRow> SigningChoices()
    {
        var settings = Services.UserSettings.Current;
        using var model = new CertificateViewModel(settings.SigningCertificate, null, OwnCertificateFile, files: settings.Files);
        var rows = model.Rows.Where(r => r.Selectable).ToList();
        // a card chosen as the default stays offered (its certificates are read only in „Zertifikat“)
        if (settings.SigningCertificate is { Source: SigningCertificateSource.Token } card && !rows.Any(r => r.Choice == card))
            rows.Add(new CertificateRow(CertificateGroup.Card, card.Name ?? Tr.T("Zertifikat auf einer Signaturkarte"), card.Path ?? "", null, null, null, card));
        return rows;
    }

    /// <param name="chosen">A certificate chosen for one signature (<see cref="DigitalSigning.With"/>); null: the default of the settings.</param>
    private async Task<SigningCertificate?> SigningCertificateAsync(SigningCertificateChoice? chosen = null)
    {
        var settings = Services.UserSettings.Current;
        var choice = chosen ?? settings.SigningCertificate ?? new SigningCertificateChoice(SigningCertificateSource.Own);
        switch (choice.Source)
        {
            case SigningCertificateSource.Store:
                try
                {
                    if (choice.Fingerprint is { } fingerprint && SigningCertificates.FromStore(fingerprint) is { } stored) return stored;
                }
                catch (System.Security.Cryptography.CryptographicException e)
                {
                    Message = Tr.T("Das Zertifikat lässt sich nicht lesen: {0}", e.Message);
                    return null;
                }
                Message = Tr.T("Das gewählte Zertifikat ist nicht mehr im Zertifikatsspeicher. Bitte unter „Zertifikat“ ein anderes wählen.");
                return null;
            case SigningCertificateSource.Token when choice.Path is { } module && choice.Fingerprint is { } card:
                return await CardAsync(module, card);
            case SigningCertificateSource.File when choice.Path is { } path:
                for (var attempt = 0; ; attempt++)
                {
                    if (attempt == MaxPasswordAttempts)
                    {
                        // a damaged file says „wrong password“ too: after a few tries, stop asking and say both
                        Message = Tr.T("Die Zertifikatsdatei lässt sich nicht öffnen: falsches Passwort, oder die Datei ist beschädigt.");
                        return null;
                    }
                    if (AskPassword is null || await AskPassword(Path.GetFileName(path), attempt > 0) is not { } password) return null;
                    try
                    {
                        return SigningCertificates.LoadFile(path, password);
                    }
                    catch (NoPrivateKeyException e)
                    {
                        Message = e.Message;
                        return null;
                    }
                    catch (System.Security.Cryptography.CryptographicException)
                    {
                        // wrong password (or a damaged file): ask again, the user can cancel
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        Message = Tr.T("Das Zertifikat lässt sich nicht lesen: {0}", Services.Failure.Text(e));
                        return null;
                    }
                }
            default:
                // one of several own certificates: its file; a choice without one (and from before) means the first (No. 733)
                var ownFile = choice.Path ?? OwnCertificateFile;
                switch (await Task.Run(() => SigningCertificates.OwnState(ownFile)))
                {
                    // with its own password: asked once per session; never replaced by a new one
                    case OwnCertificateState.Locked:
                        return await UnlockOwnAsync(ownFile);
                    case OwnCertificateState.Ready when await Task.Run(() => SigningCertificates.LoadOwn(ownFile)) is { } own:
                        return own;
                }
                // RSA key generation takes a moment: in the background, the window stays responsive
                if (settings.SignatureName is { Length: > 0 } name) return await Task.Run(() => SigningCertificates.CreateOwn(name, ownFile));
                await ManageCertificate();
                // the window may have created another own certificate and chosen it
                if (Services.UserSettings.Current.SigningCertificate is { Source: SigningCertificateSource.Own, Path: { } other }) ownFile = other;
                if (SigningCertificates.OwnState(ownFile) == OwnCertificateState.Locked) return await UnlockOwnAsync(ownFile);
                if (SigningCertificates.LoadOwn(ownFile) is { } created) return created;
                // another one chosen in the window (file or store): that one, without opening the window again
                if (Services.UserSettings.Current.SigningCertificate is { Source: not SigningCertificateSource.Own }) return await SigningCertificateAsync();
                Message = Tr.T("Zum Signieren fehlt ein Zertifikat. Bitte unter „Zertifikat“ eines erzeugen oder wählen.");
                return null;
        }
    }

    private string? _cardPin;

    /// <summary>
    /// The certificate of a signature card, logged in: the PIN of this session, otherwise asked (with a warning before the last try; up to
    /// <see cref="MaxPasswordAttempts"/> times), or at the reader's PIN pad. The PIN stays in memory until the program ends, never on disk.
    /// </summary>
    private async Task<SigningCertificate?> CardAsync(string module, string fingerprint)
    {
        for (var attempt = 0; attempt < MaxPasswordAttempts; attempt++)
        {
            Core.Platform.Pkcs11.TokenPinInfo? info;
            try
            {
                info = await Task.Run(() => Core.Platform.Pkcs11.SmartCards.PinInfo(module, fingerprint));
            }
            catch (Core.Platform.Pkcs11.Pkcs11Exception e)
            {
                Message = e.Message;
                return null;
            }
            if (info is null)
            {
                Message = Tr.T("Die Karte mit dem gewählten Zertifikat ist nicht im Leser. Bitte einlegen oder unter „Zertifikat“ ein anderes wählen.");
                return null;
            }
            if (info.Locked)
            {
                Message = Tr.T("Die PIN der Karte „{0}“ ist gesperrt. Entsperren geht nur mit der PUK, mit dem Programm des Kartenanbieters.", info.Token);
                return null;
            }
            var pin = info.PinPad ? null : _cardPin;
            if (!info.PinPad && pin is null)
            {
                var detail = Tr.T("PIN der Karte „{0}“ — bis zum Beenden gemerkt", info.Token)
                             + (info.FinalTry ? " " + Tr.T("Achtung: letzter Versuch, danach ist die Karte gesperrt.") : "");
                if (AskSessionPassword is null || await AskSessionPassword(Tr.T("Signaturkarte"), detail, Tr.T("Unterschreiben"), attempt > 0) is not { } typed)
                    return null;
                pin = typed;
            }
            if (info.PinPad) Busy = Tr.T("Bitte die PIN am Kartenleser eingeben …");
            try
            {
                var opened = await Task.Run(() => Core.Platform.Pkcs11.SmartCards.Open(module, fingerprint, pin));
                if (!info.PinPad) _cardPin = pin;
                return opened;
            }
            catch (Core.Platform.Pkcs11.Pkcs11Exception e) when (e.WrongPin)
            {
                _cardPin = null;   // asked again, with the warning before the last try
            }
            catch (Core.Platform.Pkcs11.Pkcs11Exception e)
            {
                _cardPin = null;
                Message = e.Message;
                return null;
            }
            finally
            {
                if (info.PinPad) Busy = null;
            }
        }
        Message = Tr.T("Die PIN der Signaturkarte war mehrmals falsch. Bitte die PIN prüfen; nach weiteren Fehlversuchen sperrt die Karte.");
        return null;
    }

    /// <summary>Asks for the password of the own certificate (set by the view): whether the one before was wrong → the password, or null.</summary>
    public Func<bool, Task<string?>>? AskOwnCertificatePassword { get; set; }

    /// <summary>The time-stamping authority instead of the one from the settings (set by the tests: a service that needs no network); null = the settings.</summary>
    internal Func<ITimestampAuthority?>? Timestamps { get; set; }

    /// <summary>Asks for a password kept for this session (set by the view): heading, explanation, button, whether the one before was wrong → the password, or null.</summary>
    public Func<string, string, string, bool, Task<string?>>? AskSessionPassword { get; set; }

    private string? _timestampPassword, _timestampCertificatePassword;

    /// <summary>For tests: a document time stamp after signing without the setting.</summary>
    internal bool ArchiveForTests { get; set; }

    /// <summary>For tests: answers HTTP requests to the time-stamping authority instead of the network; null = the network.</summary>
    internal HttpMessageHandler? TimestampHandler { get; set; }

    private static TimestampService? TimestampServiceOf(AppSettings settings) =>
        settings.TimestampServer is { } server ? new TimestampService(server, settings.TimestampUser, settings.TimestampCertificate) : null;

    /// <summary>A login was refused or the setup changed: the passwords of this session are asked again.</summary>
    internal void ForgetTimestampLogin() => _timestampPassword = _timestampCertificatePassword = null;

    /// <summary>
    /// The time-stamping authority for the signatures being saved: the test's, or the one from „Zertifikat“ when switched on, with its login.
    /// </summary>
    /// <returns>Whether to go on (false: a password was not given), and the authority; null for none.</returns>
    private async Task<(bool Go, ITimestampAuthority? Authority)> SigningTimestampsAsync()
    {
        if (Timestamps is { } test) return (true, test());
        var settings = Services.UserSettings.Current;
        if (!settings.SignatureTimestamps || TimestampServiceOf(settings) is not { } service || !HttpTimestampAuthority.TryParse(service.Server, out _)) return (true, null);
        var authority = await OpenTimestampAuthorityAsync(service);
        return (authority is not null, authority);
    }

    /// <summary>
    /// Opens a time-stamping authority with its login: the password of the user name and that of the customer certificate are asked once per
    /// session (a wrong certificate password up to <see cref="MaxPasswordAttempts"/> times), never stored.
    /// </summary>
    /// <param name="service">The service as set up.</param>
    /// <returns>The authority; null if the address is unusable, a password was not given, or the certificate cannot be used (said in <see cref="Message"/>).</returns>
    internal async Task<ITimestampAuthority?> OpenTimestampAuthorityAsync(TimestampService service)
    {
        if (!HttpTimestampAuthority.TryParse(service.Server, out var address)) return null;
        if (!service.HasLogin) return new HttpTimestampAuthority(address, TimestampHandler);
        string? password = null;
        if (service.User is { } user)
        {
            password = _timestampPassword ?? (AskSessionPassword is null ? null
                : await AskSessionPassword(Tr.T("Zeitstempeldienst"), Tr.T("Passwort für {0} bei {1} — bis zum Beenden gemerkt", user, address.Host), Tr.T("Anmelden"), false));
            if (password is null) return null;
            _timestampPassword = password;
        }
        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = null;
        if (service.Certificate is { } file)
        {
            for (var attempt = 0; certificate is null; attempt++)
            {
                if (attempt == MaxPasswordAttempts)
                {
                    Message = Tr.T("Das Kundenzertifikat für den Zeitstempeldienst lässt sich nicht öffnen: falsches Passwort, oder die Datei ist beschädigt.");
                    return null;
                }
                var secret = _timestampCertificatePassword ?? (AskSessionPassword is null ? null
                    : await AskSessionPassword(Tr.T("Kundenzertifikat"), Tr.T("Passwort für {0} — bis zum Beenden gemerkt", Path.GetFileName(file)), Tr.T("Anmelden"), attempt > 0));
                if (secret is null) return null;
                try
                {
                    certificate = await Task.Run(() => TimestampLogin.OpenCertificate(file, secret));
                    _timestampCertificatePassword = secret;
                }
                catch (CertificatePasswordException)
                {
                    _timestampCertificatePassword = null;
                }
                catch (Exception e) when (e is NoPrivateKeyException or IOException or UnauthorizedAccessException)
                {
                    Message = Tr.T("Das Kundenzertifikat für den Zeitstempeldienst lässt sich nicht verwenden: {0}", e.Message);
                    return null;
                }
            }
        }
        return new HttpTimestampAuthority(address, TimestampHandler, new TimestampLogin(service.User, password, certificate));
    }

    /// <summary>
    /// Where to ask for the revocation status after signing (long-term validation): the certification authorities named in the certificates
    /// when switched on in „Zertifikat“, otherwise nobody (set by the tests: services that need no network).
    /// </summary>
    internal Func<IRevocationSource?> Revocation { get; set; } = () =>
        Services.UserSettings.Current.SignatureLongTerm || Services.UserSettings.Current.SignatureArchive ? new HttpRevocationSource() : null;

    /// <summary>
    /// Where to ask for the revocation status when an archive time stamp follows and <see cref="Revocation"/> gives none: B-LTA needs the
    /// validation data before the document time stamp (EN 319 142-1, 6.3; docs/FEHLERGEDAECHTNIS.md, No. 527). Set by the tests.
    /// </summary>
    internal Func<IRevocationSource> ArchiveRevocation { get; set; } = () => new HttpRevocationSource();

    /// <summary>File of the own certificate; null = the default (tests supply their own).</summary>
    internal string? OwnCertificateFile { get; set; }

    // the passwords of the own certificates, each kept until the program ends once it was right (KONZEPT.md, section 5.17, „Passwort“);
    // per file, as there can be several (No. 733)
    private readonly Dictionary<string, string> _ownPasswords = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// An own certificate with its password: the one of this session, otherwise asked (up to <see cref="MaxPasswordAttempts"/> times).
    /// </summary>
    /// <param name="file">Its file; null = the first one.</param>
    private async Task<SigningCertificate?> UnlockOwnAsync(string? file)
    {
        var key = file ?? SigningCertificates.OwnFile;
        for (var attempt = 0; attempt < MaxPasswordAttempts; attempt++)
        {
            var password = _ownPasswords.GetValueOrDefault(key);
            if (password is null)
            {
                if (AskOwnCertificatePassword is null || await AskOwnCertificatePassword(attempt > 0) is not { } typed) return null;
                password = typed;
            }
            try
            {
                var own = await Task.Run(() => SigningCertificates.LoadOwn(file, password));
                _ownPasswords[key] = password;
                if (own is not null) return own;
                Message = Tr.T("Das eigene Zertifikat lässt sich nicht lesen. Bitte unter „Zertifikat“ ein neues erzeugen.");
                return null;
            }
            catch (CertificatePasswordException)
            {
                _ownPasswords.Remove(key);
            }
        }
        Message = Tr.T("Das Passwort des eigenen Zertifikats stimmt nicht. Vergessen? Unter „Zertifikat“ lässt sich ein neues erzeugen; schon gesetzte Unterschriften bleiben gültig.");
        return null;
    }

    /// <summary>Lets the user choose an image file (set by the view); returns the path, or null.</summary>
    public Func<Task<string?>>? PickImageFile { get; set; }

    /// <summary>The signature used last, from the settings.</summary>
    private static SignatureElement LastSignature()
    {
        var s = Services.UserSettings.Current;
        var fonts = Core.Fonts.EmbeddedFonts.Handwriting;
        return new SignatureElement
        {
            Rect = default,
            Name = s.SignatureName ?? "",
            FontFile = s.SignatureFont is { } f && fonts.Contains(f) ? f : fonts.FirstOrDefault() ?? "",
            Color = s.SignatureColor,
            Textured = s.SignatureTextured,
            InkStrength = s.SignatureInkStrength,
        };
    }

    private static void Remember(PageElement chosen)
    {
        switch (chosen)
        {
            case SignatureElement s:
                Services.UserSettings.Update(x => x with { SignatureName = s.Name, SignatureFont = s.FontFile, SignatureColor = s.Color, SignatureTextured = s.Textured, SignatureInkStrength = s.InkStrength });
                break;
            case InkElement i:
                Services.UserSettings.Update(x => x with { SignatureColor = i.Color, SignatureTextured = i.Textured, SignatureInkStrength = i.InkStrength });
                break;
        }
    }

    /// <summary>„Unterschrift“: choose name and handwriting, then click on the page or drag a rectangle.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task InsertSignature()
    {
        if (!EnsureAllowed("signature.sign")) return;
        if (SelectedDocument is not { } doc || PickSignature is null) return;
        if (!doc.CanPlace(ViewTool.InsertSignature))
        {
            Message = Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen.");
            return;
        }
        if (await PickSignature(LastSignature()) is not { } chosen) return;
        Remember(chosen);
        ElementDefaults.Signature = chosen;
        Tool = ViewTool.InsertSignature;
    }

    /// <summary>Double-click on a signature: choose again; the position stays.</summary>
    private async void OnSignatureEditRequested(ElementRef target)
    {
        if (PickSignature is null || SelectedDocument is not { } doc) return;
        if (target.Element is not (SignatureElement or InkElement or ImageElement) || await PickSignature(target.Element) is not { } chosen) return;
        Remember(chosen);
        // New kind or new content at the same spot: height and centre stay, the width follows the content
        var updated = chosen with { Rect = target.Element.Rect, Rotation = target.Element.Rotation };
        // the old element goes, the new one comes: the rights of both (an image is an annotation, a signature fixed in the page)
        if (!doc.CanChange(target.Element) || !doc.CanChange(updated))
        {
            Message = Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen.");
            return;
        }
        doc.ReplaceElement(target, ElementAppearance.FitToAspect(updated), Tr.T("Unterschrift ändern"));
    }

    /// <summary>„Bild“: choose a file, then click on the page or drag a rectangle (the background stays).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task InsertImage()
    {
        if (!EnsureAllowed("insert.images")) return;
        if (SelectedDocument is not { } doc || PickImageFile is null) return;
        if (!doc.CanPlace(ViewTool.InsertImage))
        {
            Message = Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen.");
            return;
        }
        if (await PickImageFile() is not { } path) return;
        // Large photos take a few tenths of a second: not on the UI thread
        var image = await Task.Run(() =>
        {
            try
            {
                return Services.ImageLoader.Load(path, Services.ImageLoader.MaxPictureSide);   // nearly full size: saving uses 300 dpi
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });
        if (image is null)
        {
            Message = Tr.T("Das Bild lässt sich nicht lesen.");
            return;
        }
        ElementDefaults.Image = new ImageElement { Rect = default, Image = image };
        Tool = ViewTool.InsertImage;
    }

    // ------------------------------------------------------------ Shapes

    /// <summary>The shape currently chosen (for the buttons); null when the tool is not active.</summary>
    public ShapeKind? ActiveShape => Tool == ViewTool.InsertShape ? ElementDefaults.Shape.Kind : null;

    /// <summary>„Formen“: choose the shape, then drag or click; the same button again switches back.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void InsertShape(ShapeKind kind)
    {
        if (!EnsureAllowed("insert.shapes")) return;
        if (SelectedDocument is not { } doc) return;
        if (!doc.CanPlace(ViewTool.InsertShape))
        {
            Message = Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen.");
            return;
        }
        if (Tool == ViewTool.InsertShape && ElementDefaults.Shape.Kind == kind)
        {
            Tool = ViewTool.SelectText;
            return;
        }
        ElementDefaults.Shape = ElementDefaults.Shape with { Kind = kind };
        if (Tool == ViewTool.InsertShape) OnPropertyChanged(nameof(ActiveShape));
        else Tool = ViewTool.InsertShape;
    }

    // ------------------------------------------------------------ Codes

    /// <summary>
    /// „Code“ (KONZEPT.md, section 5.19): click or drag a rectangle; the new code is selected and its content typed in the panel on the
    /// right. The same button again switches back.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void InsertCode()
    {
        if (!EnsureAllowed("insert.codes")) return;
        if (SelectedDocument is not { } doc) return;
        if (!doc.CanPlace(ViewTool.InsertCode))
        {
            Message = Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen.");
            return;
        }
        Tool = Tool == ViewTool.InsertCode ? ViewTool.SelectText : ViewTool.InsertCode;
    }

    /// <summary>Einfügen → „Notiz“: the next click on a page places a note; its text is written in the area „Notizen“.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void InsertNote()
    {
        if (!EnsureAllowed("insert.notes")) return;
        if (SelectedDocument is not { } doc) return;
        if (!doc.CanPlace(ViewTool.InsertNote))
        {
            Message = Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen.");
            return;
        }
        Tool = Tool == ViewTool.InsertNote ? ViewTool.SelectText : ViewTool.InsertNote;
    }

    /// <summary>Einfügen → „Messen“: lengths, areas, angles and radii with the scale of the page (Ablaufplan 7.6); a second click ends it.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Measure()
    {
        if (SelectedDocument is null) return;
        if (Tool != ViewTool.Measure && !EnsureAllowed("start.measure")) return;
        Tool = Tool == ViewTool.Measure ? ViewTool.SelectText : ViewTool.Measure;
    }

    // ------------------------------------------------------------ Layers

    private bool CanMoveLayer => SelectedDocument?.CanMoveLayer == true;

    /// <summary>Moves the selected elements forward or backward. See KONZEPT.md, section 5.13.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveLayer))]
    private void MoveLayer(LayerMove move) => SelectedDocument?.MoveLayer(move);

    /// <summary>Whether layer numbers are shown on the elements (1 = backmost).</summary>
    public bool ShowLayers
    {
        get => SelectedDocument?.ShowLayers == true;
        set
        {
            SelectedDocument?.SetShowLayers(value);
            OnPropertyChanged();
        }
    }

    // ------------------------------------------------------------ Flattening

    /// <summary>Whether the selected document is flattened on save (button in the „Sicher“ tab).</summary>
    public bool FlattenOnSave
    {
        get => SelectedDocument?.FlattenOnSave == true;
        set
        {
            // „Flachlegen“ can be switched off by the function file of the organization
            if (value && !EnsureAllowed("secure.flatten"))
            {
                OnPropertyChanged();
                return;
            }
            // fields, comments and stamps go into the page content: bit 4 (ISO 32000-2, Table 22; docs/FEHLERGEDAECHTNIS.md, No. 1111)
            if (value && SelectedDocument is { } restricted && !CheckRight(restricted.CanModifyContent))
            {
                OnPropertyChanged();
                return;
            }
            if (SelectedDocument is { } doc) doc.FlattenOnSave = value;
            OnPropertyChanged();
        }
    }

    /// <summary>After signing: bar asking whether to flatten.</summary>
    [ObservableProperty] public partial bool FlattenPromptVisible { get; set; }

    private void OnFlattenSuggested() => FlattenPromptVisible = true;

    [RelayCommand]
    private void FlattenNow()
    {
        FlattenOnSave = true;
        FlattenPromptVisible = false;
    }

    [RelayCommand]
    private void FlattenLater() => FlattenPromptVisible = false;
}
