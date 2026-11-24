using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// Password to open and permissions (printing, copying, modifying …) for the next save. Passwords are never
/// prefilled. Result: new protection, remove protection (Protection null), or null (cancelled, change nothing). Instead of passwords the
/// document can be encrypted for recipients — their certificates, each with all rights or the rights chosen below (ISO 32000-2, 7.6.5;
/// docs/FEHLERGEDAECHTNIS.md, No. 1117).
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
        RecipientMode.IsCheckedChanged += (_, _) =>
        {
            CipherText.Text = RecipientMode.IsChecked == true ? Tr.T("Verschlüsselung: AES-256 für Empfänger (ab PDF 1.7)") : _passwordCipherText;
            UpdateLengthHint();
        };
        _passwordCipherText = CipherText.Text;
        Opened += (_, _) => OpenBox.Focus();
    }

    private readonly string? _passwordCipherText;

    /// <summary>The recipients added: certificate and its „Alle Rechte“ box.</summary>
    internal readonly List<(X509Certificate2 Certificate, CheckBox FullAccess)> Recipients = [];

    /// <summary>Adds a recipient (also for the tests): a certificate with an RSA key, each only once; returns an error text or null.</summary>
    internal string? AddRecipient(X509Certificate2 certificate)
    {
        if (certificate.GetRSAPublicKey() is null)
            return Tr.T("Das Zertifikat „{0}“ hat keinen RSA-Schlüssel; für Empfänger wird nur mit RSA verschlüsselt.", NameOf(certificate));
        if (Recipients.Any(r => r.Certificate.Thumbprint == certificate.Thumbprint)) return Tr.T("„{0}“ ist schon Empfänger.", NameOf(certificate));
        var full = new CheckBox { Content = Tr.T("Alle Rechte"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var remove = new Button { Content = "×", Padding = new Avalonia.Thickness(8, 0) };
        Avalonia.Automation.AutomationProperties.SetName(remove, Tr.T("„{0}“ entfernen", NameOf(certificate)));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,8,Auto") };
        var name = new TextBlock { Text = NameOf(certificate), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(name, certificate.Subject);
        row.Children.Add(name);
        Grid.SetColumn(full, 1);
        row.Children.Add(full);
        Grid.SetColumn(remove, 3);
        row.Children.Add(remove);
        var entry = (certificate, full);
        remove.Click += (_, _) =>
        {
            Recipients.Remove(entry);
            RecipientRows.Children.Remove(row);
            NoRecipients.IsVisible = Recipients.Count == 0;
        };
        Recipients.Add(entry);
        RecipientRows.Children.Add(row);
        NoRecipients.IsVisible = false;
        return null;
    }

    private static string NameOf(X509Certificate2 certificate) =>
        certificate.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } name ? name : certificate.Subject;

    private async void OnAddRecipient(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T("Zertifikat eines Empfängers wählen"),
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(Tr.T("Zertifikat")) { Patterns = ["*.cer", "*.crt", "*.pem", "*.der", "*.CER", "*.CRT", "*.PEM", "*.DER"] }, FilePickerFileTypes.All],
        });
        var problems = new List<string>();
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is not { } path) continue;
            try
            {
                if (AddRecipient(X509CertificateLoader.LoadCertificateFromFile(path)) is { } problem) problems.Add(problem);
            }
            catch (CryptographicException)
            {
                problems.Add(Tr.T("{0} ist kein lesbares Zertifikat.", Path.GetFileName(path)));
            }
        }
        ErrorText.Text = string.Join(" ", problems);
        ErrorText.IsVisible = problems.Count > 0;
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
            if (_aes256 || RecipientMode.IsChecked == true) return false;
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
        if (RecipientMode.IsChecked == true) return Recipients.Count == 0 ? Tr.T("Bitte mindestens ein Zertifikat eines Empfängers hinzufügen.") : null;
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
        if (RecipientMode.IsChecked == true)
        {
            // those with „Alle Rechte“ in one group (bit 2: also the change of the encryption), the others with the rights below
            var groups = new List<PdfRecipientGroup>();
            var full = Recipients.Where(r => r.FullAccess.IsChecked == true).Select(r => r.Certificate).ToList();
            var limited = Recipients.Where(r => r.FullAccess.IsChecked != true).Select(r => r.Certificate).ToList();
            if (full.Count > 0) groups.Add(new PdfRecipientGroup(full, PdfPermissions.All, FullAccess: true));
            if (limited.Count > 0) groups.Add(new PdfRecipientGroup(limited, RightsBox.IsChecked == true ? Permissions() : PdfPermissions.All));
            return new ProtectionChange(null, Recipients: groups);
        }
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
