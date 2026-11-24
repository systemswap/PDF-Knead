using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// Password to open and permissions (printing, copying, modifying …) for the next save. Passwords are never
/// prefilled. Result: new protection, remove protection (Protection null), or null (cancelled, change nothing).
/// </summary>
public partial class ProtectionWindow : Window
{
    private readonly bool _isProtected;

    public ProtectionWindow() => InitializeComponent();

    /// <param name="isProtected">The file is (after the next save) encrypted; then “Remove protection” is offered.</param>
    /// <param name="permissions">Current permissions used to prefill the check boxes.</param>
    /// <param name="version">PDF version used when saving (determines the cipher).</param>
    public ProtectionWindow(bool isProtected, PdfPermissions permissions, string version) : this()
    {
        _isProtected = isProtected;
        _aes256 = PdfProtection.CipherFor(version) == PdfCipher.Aes256;
        RemoveButton.IsVisible = isProtected;
        var cipher = PdfProtection.CipherFor(version) switch
        {
            PdfCipher.Aes256 => "AES-256",
            PdfCipher.Aes128 => "AES-128",
            _ => "RC4 128 Bit",
        };
        CipherText.Text = Tr.T("Verschlüsselung: {0} (PDF {1})", cipher, version);
        PrintChoice.ItemsSource = new[] { Tr.T("Nicht erlaubt"), Tr.T("Niedrige Auflösung"), Tr.T("Hohe Auflösung") };
        PrintChoice.SelectedIndex = permissions.HasFlag(PdfPermissions.PrintHighQuality) && permissions.HasFlag(PdfPermissions.Print) ? 2
            : permissions.HasFlag(PdfPermissions.Print) ? 1 : 0;
        CopyBox.IsChecked = permissions.HasFlag(PdfPermissions.Copy);
        ModifyBox.IsChecked = permissions.HasFlag(PdfPermissions.Modify);
        AnnotateBox.IsChecked = permissions.HasFlag(PdfPermissions.Annotate);
        FormsBox.IsChecked = permissions.HasFlag(PdfPermissions.FillForms);
        AssembleBox.IsChecked = permissions.HasFlag(PdfPermissions.Assemble);
        RightsBox.IsChecked = ((permissions | PdfPermissions.Accessibility) & PdfPermissions.All) != PdfPermissions.All;
        OpenPassword.TextChanged += (_, _) => UpdateLengthHint();
        OwnerPassword.TextChanged += (_, _) => UpdateLengthHint();
        OpenBox.IsCheckedChanged += (_, _) => UpdateLengthHint();
        RightsBox.IsCheckedChanged += (_, _) => UpdateLengthHint();
        UpdateLengthHint();
        Opened += (_, _) => OpenBox.Focus();
    }

    /// <summary>
    /// Whether a password in use is cut to 32 characters on saving: RC4 and AES-128 pad or truncate it to 32 bytes (ISO 32000-2, 7.6.4.3.2,
    /// algorithm 2, step a). Not with AES-256, and not where a password does not fit PDFDocEncoding — then the copy is written with AES-256
    /// anyway (see the writer's choice of version).
    /// </summary>
    internal bool PasswordTruncated
    {
        get
        {
            if (_aes256) return false;
            var passwords = new[] { OpenBox.IsChecked == true ? OpenPassword.Text ?? "" : "", RightsBox.IsChecked == true ? OwnerPassword.Text ?? "" : "" };
            if (!passwords.All(StandardSecurityHandler.FitsLegacyPassword)) return false;
            return passwords.Any(p => p.Length > 32);
        }
    }

    /// <summary>Shows or hides the hint that only the first 32 characters count.</summary>
    private void UpdateLengthHint() => LengthHint.IsVisible = PasswordTruncated;

    /// <summary>Permissions from the check boxes.</summary>
    private PdfPermissions Permissions()
    {
        var p = PrintChoice.SelectedIndex switch
        {
            2 => PdfPermissions.Print | PdfPermissions.PrintHighQuality,
            1 => PdfPermissions.Print,
            _ => PdfPermissions.None,
        };
        if (CopyBox.IsChecked == true) p |= PdfPermissions.Copy;
        if (ModifyBox.IsChecked == true) p |= PdfPermissions.Modify;
        if (AnnotateBox.IsChecked == true) p |= PdfPermissions.Annotate;
        if (FormsBox.IsChecked == true) p |= PdfPermissions.FillForms;
        if (AssembleBox.IsChecked == true) p |= PdfPermissions.Assemble;
        // text for screen readers is always allowed (ISO 32000-2, 7.6.4.2; docs/FEHLERGEDAECHTNIS.md, No. 490)
        return p | PdfPermissions.Accessibility;
    }

    /// <summary>Validates the input; returns an error text, or null if the input is valid.</summary>
    internal string? Validate()
    {
        var open = OpenBox.IsChecked == true;
        var rights = RightsBox.IsChecked == true;
        if (open && string.IsNullOrEmpty(OpenPassword.Text)) return Tr.T("Bitte ein Passwort zum Öffnen eingeben.");
        if (open && OpenPassword.Text != OpenRepeat.Text) return Tr.T("Die beiden Passwörter zum Öffnen stimmen nicht überein.");
        if (rights && string.IsNullOrEmpty(OwnerPassword.Text)) return Tr.T("Bitte ein Rechte-Passwort eingeben — damit lassen sich die Rechte später wieder ändern.");
        if (rights && OwnerPassword.Text != OwnerRepeat.Text) return Tr.T("Die beiden Rechte-Passwörter stimmen nicht überein.");
        if (open && rights && OpenPassword.Text == OwnerPassword.Text)
            return Tr.T("Das Rechte-Passwort muss sich vom Passwort zum Öffnen unterscheiden — sonst gälten mit dem Öffnen alle Rechte.");
        // AES-256: characters a conforming reader refuses (SASLprep, ISO 32000-2, algorithm 2.A; docs/FEHLERGEDAECHTNIS.md, No. 558)
        if (_aes256 && open && StandardSecurityHandler.SaslRefusal(OpenPassword.Text ?? "") is { } openRefusal) return openRefusal;
        if (_aes256 && rights && StandardSecurityHandler.SaslRefusal(OwnerPassword.Text ?? "") is { } ownerRefusal) return ownerRefusal;
        return null;
    }

    private readonly bool _aes256;

    /// <summary>Result according to the input (without validation).</summary>
    internal ProtectionChange? Result()
    {
        var open = OpenBox.IsChecked == true;
        var rights = RightsBox.IsChecked == true;
        if (!open && !rights) return _isProtected ? new ProtectionChange(null) : null;   // nothing selected: no protection
        return new ProtectionChange(new PdfProtection(
            open ? OpenPassword.Text ?? "" : "",
            rights ? OwnerPassword.Text ?? "" : "",
            rights ? Permissions() : PdfPermissions.All));
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (Validate() is { } error)
        {
            ErrorText.Text = error;
            ErrorText.IsVisible = true;
            return;
        }
        Close(Result());
    }

    private void OnRemove(object? sender, RoutedEventArgs e) => Close(new ProtectionChange(null));

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
