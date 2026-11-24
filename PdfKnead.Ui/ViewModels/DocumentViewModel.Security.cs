using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.ViewModels;

/// <summary>
/// A pending change of protection: new passwords and permissions, or (Protection null) removal of the protection; with
/// <paramref name="AllRights"/> the passwords stay and only the restrictions are dropped (Protection is then null); with
/// <paramref name="Recipients"/> an encryption for recipients instead of passwords (ISO 32000-2, 7.6.5; docs/FEHLERGEDAECHTNIS.md, No. 1117).
/// </summary>
public sealed record ProtectionChange(PdfProtection? Protection, bool AllRights = false, IReadOnlyList<PdfRecipientGroup>? Recipients = null);

/// <summary>A permission of the file and whether it is allowed (shown with a tick or a cross).</summary>
public sealed record RightRow(string Label, bool Allowed);

/// <summary>Reply of the „Einschränkungen entfernen“ window: whether to keep the open password (if the file has one).</summary>
public sealed record RestrictionsReply(bool KeepOpenPassword);

/// <summary>Password and permissions, „Sicher“ tab.</summary>
public sealed partial class DocumentViewModel
{
    /// <summary>For the next save: new protection or removal of protection; null = as the file has it. Counts as a modification.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified), nameof(Title), nameof(IsProtected))]
    public partial ProtectionChange? PendingProtection { get; set; }

    /// <summary>Whether the file is encrypted (after the next save).</summary>
    public bool IsProtected => PendingProtection is { } change
        ? change.Protection is not null || change.Recipients is { Count: > 0 } || change.AllRights
        : Document.Security is not null;

    /// <summary>
    /// Why „Digital signieren“ is not possible for this document, or null if it is: encrypted files are signed (an encrypted update with the
    /// file's key) — not with RC4, not where ISO/TS 32004 asks for a MAC, not without the right to fill in or annotate when the file was
    /// opened without the owner password (docs/ABLAUFPLAN.md, 4.4 e; docs/FEHLERGEDAECHTNIS.md, No. 543).
    /// </summary>
    public string? DigitalSigningRefusal
    {
        get
        {
            // new protection on saving: written by PDF Knead with its own key, RC4 only below PDF 1.6
            if (PendingProtection is { } change)
                return change.Protection is not null && PdfProtection.CipherFor(SaveVersion) == PdfCipher.Rc4
                    ? Tr.T("Mit RC4 verschlüsselte Dateien lassen sich nicht signieren. Bitte zuerst mit neuem Schutz (AES) speichern.") : null;
            if (Document.Security is null) return null;
            if (Core.Pdf.Writing.IncrementalUpdate.EncryptionRefusal(Document) is { } refusal) return refusal;
            // signing fills a signature field: bit 6, from revision 3 also bit 9 (Table 22; No. 1111)
            return !HasAllRights && !CanFillForms ? Tr.T("Die Rechte dieser Datei erlauben kein Unterschreiben.") : null;
        }
    }

    /// <summary>
    /// Whether all permissions apply: without encryption, when opened with the owner password, or after „Einschränkungen entfernen“
    /// (which takes effect immediately in the open file, and in the file itself from the next save).
    /// </summary>
    public bool HasAllRights =>
        Document.Security is not { } s || s.IsOwner || (s.Permissions & PdfPermissions.All) == PdfPermissions.All || RestrictionsLifted;

    /// <summary>Whether „Einschränkungen entfernen“ has been done (until the next save; after that it is in the file).</summary>
    public bool RestrictionsLifted { get; private set; }

    /// <summary>Permissions the file itself grants (/P), even when it is opened with the owner password and therefore everything is allowed.</summary>
    public PdfPermissions FilePermissions =>
        Document.EncryptDictionary?.GetInteger(Core.Pdf.Objects.PdfName.Get("P")) is { } p ? (PdfPermissions)(int)p & PdfPermissions.All : PdfPermissions.All;

    /// <summary>Whether there are restrictions that „Einschränkungen entfernen“ can lift (also when opened with the owner password).</summary>
    public bool HasRestrictions => Document.Security is not null && !RestrictionsLifted && FilePermissions != PdfPermissions.All;

    /// <summary>
    /// Whether the file has an owner password that was not given — also when it permits everything: then nothing is locked, but without
    /// „Einschränkungen entfernen“ the password could never be removed, since changing the protection needs it (CanChangeProtection).
    /// </summary>
    public bool HasPermissionPassword => Document.Security is { IsOwner: false } && !RestrictionsLifted;

    /// <summary>Whether „Einschränkungen entfernen“ has something to do: restrictions, or an owner password that was not given.</summary>
    public bool CanLiftRestrictions => HasRestrictions || HasPermissionPassword;

    /// <summary>
    /// Protection may be changed if the file was opened without encryption or with the owner password, or the restrictions have been
    /// removed; having all permissions with the open password is not enough (otherwise the open password could be dropped silently).
    /// </summary>
    public bool CanChangeProtection => Document.Security is not { } s || s.IsOwner || RestrictionsLifted;

    /// <summary>Permissions the file grants (after the next save), used to prefill the window.</summary>
    public PdfPermissions CurrentPermissions =>
        PendingProtection is { Protection: { } p } ? p.Permissions
        : PendingProtection is { AllRights: true } ? PdfPermissions.All
        : Document.Security?.Permissions ?? PdfPermissions.All;

    /// <summary>What the file locks, in readable form („Drucken, Kopieren …“), for the „Einschränkungen entfernen“ window.</summary>
    public string LockedRightsText => string.Join(", ", RightsOf(FilePermissions).Where(r => !r.Allowed).Select(r => r.Label));

    /// <summary>The permissions a file can grant, each allowed or locked by <paramref name="p"/>, in readable form.</summary>
    private static List<RightRow> RightsOf(PdfPermissions p) =>
    [
        new(Tr.T("Drucken"), p.HasFlag(PdfPermissions.Print)),
        new(Tr.T("Drucken in hoher Auflösung"), p.HasFlag(PdfPermissions.Print) && p.HasFlag(PdfPermissions.PrintHighQuality)),
        new(Tr.T("Kopieren"), p.HasFlag(PdfPermissions.Copy)),
        new(Tr.T("Ändern"), p.HasFlag(PdfPermissions.Modify)),
        new(Tr.T("Kommentieren"), p.HasFlag(PdfPermissions.Annotate)),
        new(Tr.T("Formulare ausfüllen"), p.HasFlag(PdfPermissions.FillForms)),
        new(Tr.T("Seiten zusammenstellen"), p.HasFlag(PdfPermissions.Assemble)),
    ];

    /// <summary>
    /// The permissions of the file, each allowed or locked („Berechtigungen“ in the „Sicher“ tab and „Eigenschaften“, wish of 4.11.2026):
    /// what the file itself grants (/P) — also when it is opened with the owner password, where everything is allowed in the program.
    /// </summary>
    public IReadOnlyList<RightRow> Rights => RightsOf(Document.Security is null || RestrictionsLifted ? PdfPermissions.All : FilePermissions);

    /// <summary>A line under the permissions: no protection, restrictions lifted, or opened with the owner password.</summary>
    public string RightsNote =>
        Document.Security is null ? Tr.T("Die Datei hat keine Einschränkungen.")
        : RestrictionsLifted ? Tr.T("Einschränkungen entfernt — in der Datei ab dem nächsten Speichern.")
        : FilePermissions == PdfPermissions.All ? Tr.T("Die Datei erlaubt alles.")
        : Document.Security.IsOwner ? Tr.T("Mit dem Rechte-Passwort geöffnet: hier ist trotzdem alles erlaubt.")
        : Tr.T("Gesperrtes lässt sich mit „Einschränkungen entfernen“ wieder erlauben.");

    /// <summary>Whether the file needs a password to open (not just for permissions).</summary>
    public bool HasOpenPassword =>
        Document.Security is { } && Document.EncryptDictionary is { } encrypt &&
        !StandardSecurityHandler.OpensWithoutPassword(encrypt, Document.Trailer.Get(Core.Pdf.Objects.PdfName.ID) as Core.Pdf.Objects.PdfArray);

    /// <summary>Checks whether <paramref name="password"/> is the owner password of the open file.</summary>
    /// <param name="password">The password to check; an empty one never matches.</param>
    /// <returns>true if it is the owner password.</returns>
    public bool IsOwnerPassword(string password) =>
        Document.EncryptDictionary is { } encrypt && password.Length > 0 &&
        StandardSecurityHandler.IsOwnerPassword(encrypt, Document.Trailer.Get(Core.Pdf.Objects.PdfName.ID) as Core.Pdf.Objects.PdfArray, password);

    /// <summary>
    /// Removes the restrictions: takes effect immediately in the open file and is saved with all permissions on the next save. With
    /// <paramref name="keepOpenPassword"/> and an existing open password the passwords stay; otherwise the file is saved without
    /// protection. The window has already established beforehand that this is permitted (owner password or explicit confirmation).
    /// See KONZEPT.md, section 5.11.
    /// </summary>
    /// <param name="keepOpenPassword">Keep the open password if the file has one.</param>
    public void LiftRestrictions(bool keepOpenPassword)
    {
        if (Document.Security is null) return;
        var keep = keepOpenPassword && HasOpenPassword && Document.Security.CanGrantAllPermissions;
        PendingProtection = new ProtectionChange(null, AllRights: keep);
        RestrictionsLifted = true;
        foreach (var name in new[]
                 {
                     nameof(HasAllRights), nameof(RestrictionsLifted), nameof(HasRestrictions), nameof(HasPermissionPassword),
                     nameof(CanLiftRestrictions), nameof(CanChangeProtection),
                     nameof(CanEditPages), nameof(CanEditElements), nameof(CanFillForms), nameof(CanModifyContent), nameof(CanAnnotate),
                     nameof(CanDesignForms), nameof(CanRedact), nameof(CanEditTextFormat), nameof(CanMoveLayer), nameof(CopyAllowed), nameof(Properties),
                     nameof(Rights), nameof(RightsNote),
                 })
            OnPropertyChanged(name);
    }

    /// <summary>Builds the options for saving (version, flattening, protection).</summary>
    /// <param name="version">PDF version to write.</param>
    /// <returns>The save options.</returns>
    public PdfSaveOptions SaveOptions(string version) => new()
    {
        Version = version,
        Flatten = FlattenOnSave,
        Protection = PendingProtection?.Protection,
        Recipients = PendingProtection?.Recipients,
        KeepEncryption = PendingProtection is null or { AllRights: true },
        GrantAllPermissions = PendingProtection is { AllRights: true },
        Cleanup = Cleaned,   // what was cleaned up in the document stays cleaned up when saving
        AttachedFileMetadataStays = AttachedMetadataKept,   // … and what an e-invoice kept needs in the metadata stays (decision (4), 15.11.2026)
        ImageCodec = Rendering.SkiaImageCodec.Instance,   // redaction: JPEG images too, pixel-exact
        OutlinesLeftOut = _outlinesLeftOut.Count > 0 ? _outlinesLeftOut : null,   // merged without their bookmarks
        // title, language, showing the title (No. 492); cleaned up: the title stays unless „Titel behalten“ is unticked
        DocumentTitle = DescriptionOnSave?.Title,
        DocumentLanguage = DescriptionOnSave?.Language,
        DisplayDocTitle = DescriptionOnSave?.DisplayTitle,
        KeepTitle = KeepTitleOnCleanup,
        RemoveAccessibilityClaim = RemoveClaimOnSave,   // „Kennung entfernen“ in „Barrierefreiheit“
        NameLayerConfigurations = NameLayersOnSave,   // „Ebenen-Ansichten benennen“ there
    };

    /// <summary>On the next save, save all pages as images; null = no. Counts as a modification.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified), nameof(Title))]
    public partial Rendering.RasterSettings? RasterOnSave { get; set; }

    /// <summary>Password with which the saved file is reopened: the owner password (all permissions), otherwise the open password.</summary>
    private string? PasswordAfterSaving(PdfSaveOptions options) => options.Protection switch
    {
        { OwnerPassword.Length: > 0 } p => p.OwnerPassword,
        { UserPassword.Length: > 0 } p => p.UserPassword,
        not null => null,
        null => options.KeepEncryption ? _password : null,
    };
}
