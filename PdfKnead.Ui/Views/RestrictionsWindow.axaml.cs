using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// “Remove restrictions” without the permissions password (see KONZEPT.md, section 5.11): either enter the permissions password or
/// explicitly confirm being entitled to do so; until then “Remove” stays disabled. Result: the reply, or null if cancelled.
/// </summary>
public partial class RestrictionsWindow : Window
{
    private readonly Func<string, bool>? _isOwnerPassword;
    private bool _ownerOk;

    public RestrictionsWindow() => InitializeComponent();

    /// <param name="fileName">Name of the file (header line).</param>
    /// <param name="locked">Readable description of what the file restricts.</param>
    /// <param name="hasOpenPassword">The file needs a password to open; then the option “Also remove the password to open” is shown.</param>
    /// <param name="isOwnerPassword">Checks an entered permissions password.</param>
    public RestrictionsWindow(string fileName, string locked, bool hasOpenPassword, Func<string, bool> isOwnerPassword) : this()
    {
        _isOwnerPassword = isOwnerPassword;
        FileText.Text = fileName;
        LockedText.Text = Tr.T("Die Datei sperrt: {0}.", locked);
        RemoveOpenBox.IsVisible = hasOpenPassword;
        // Check on every change: TextChanged arrives only via the dispatcher, so Enter right after typing would see the previous state.
        OwnerPassword.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) CheckOwner();
        };
        ConsentBox.IsCheckedChanged += (_, _) => Update();
        Opened += (_, _) => OwnerPassword.Focus();
    }

    /// <summary>Whether removing is allowed (the permissions password is correct or the confirmation is checked).</summary>
    internal bool CanLift => _ownerOk || ConsentBox.IsChecked == true;

    private void CheckOwner()
    {
        var text = OwnerPassword.Text ?? "";
        _ownerOk = text.Length > 0 && _isOwnerPassword?.Invoke(text) == true;
        OwnerState.IsVisible = text.Length > 0;
        OwnerState.Text = _ownerOk ? Tr.T("Das Rechte-Passwort stimmt.") : Tr.T("Das ist nicht das Rechte-Passwort.");
        OwnerState.Foreground = (Avalonia.Media.IBrush?)this.FindResource(_ownerOk ? "GoodBrush" : "DangerBrush");
        Update();
    }

    private void Update()
    {
        ConsentBox.IsEnabled = !_ownerOk;
        LiftButton.IsEnabled = CanLift;
    }

    private void OnLift(object? sender, RoutedEventArgs e)
    {
        if (!CanLift) return;
        Close(new RestrictionsReply(KeepOpenPassword: RemoveOpenBox.IsChecked != true));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
