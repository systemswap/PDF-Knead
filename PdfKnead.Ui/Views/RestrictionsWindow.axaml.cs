using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.Core.Policy;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// “Remove restrictions” without the permissions password (see KONZEPT.md, section 5.11): either enter the permissions password or
/// explicitly confirm being entitled to do so; until then “Remove” stays disabled. Ticking the confirmation asks once more, with the hint on
/// the law, and stays ticked only after „Ich bin berechtigt“; the function file can switch removing without the password off
/// („Ohne Rechte-Passwort entfernen“, decided by the user on 22.11.2026). Result: the reply, or null if cancelled.
/// </summary>
public partial class RestrictionsWindow : Window
{
    private readonly Func<string, bool>? _isOwnerPassword;
    private bool _ownerOk;

    /// <summary>The function file entry for removing without the permissions password.</summary>
    internal const string WithoutPasswordId = "secure.remove-restrictions-without-password";

    /// <summary>Whether the function file allows removing without the permissions password.</summary>
    private readonly bool _withoutPasswordAllowed = ActivePolicy.Allows(WithoutPasswordId);

    /// <summary>Asks before the confirmation is ticked: true for „Ich bin berechtigt“ (tests set their own).</summary>
    internal Func<Task<bool>> ConfirmEntitled { get; set; }

    public RestrictionsWindow()
    {
        InitializeComponent();
        ConfirmEntitled = async () => await new ChoiceWindow(new ChoiceRequest(Tr.T("Einschränkungen ohne Passwort entfernen"),
            Tr.T("Damit werden die Rechte der Datei (z. B. Drucken, Kopieren, Ändern) aufgehoben, ohne dass Sie das Rechte-Passwort kennen. Das ist nur erlaubt, wenn es Ihre eigene Datei ist oder der Eigentümer bzw. Rechteinhaber zugestimmt hat. Andernfalls kann es je nach Land gegen Gesetze verstoßen, etwa gegen das Urheberrecht (Umgehen von Schutzmaßnahmen)."), [Tr.T("Ich bin berechtigt")], Tr.T("Abbrechen"), Icon: "Icon.LockOpen")).ShowDialog<ChoiceAnswer>(this) is { Answer: 1 };
    }

    /// <param name="fileName">Name of the file (header line).</param>
    /// <param name="locked">Readable description of what the file restricts.</param>
    /// <param name="hasOpenPassword">The file needs a password to open; then the option “Also remove the password to open” is shown.</param>
    /// <param name="isOwnerPassword">Checks an entered permissions password.</param>
    public RestrictionsWindow(string fileName, string locked, bool hasOpenPassword, Func<string, bool> isOwnerPassword) : this()
    {
        _isOwnerPassword = isOwnerPassword;
        FileText.Text = fileName;
        // a file that permits everything and only has an owner password: nothing to name as locked
        LockedText.Text = locked.Length == 0 ? Tr.T("Die Datei sperrt nichts, sie hat nur ein Passwort für die Rechte.") : Tr.T("Die Datei sperrt: {0}.", locked);
        RemoveOpenBox.IsVisible = hasOpenPassword;
        // Check on every change: TextChanged arrives only via the dispatcher, so Enter right after typing would see the previous state.
        OwnerPassword.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) CheckOwner();
        };
        ConsentBox.IsCheckedChanged += (_, _) => Update();
        ConsentBox.Click += async (_, _) => await OnConsentClicked();
        ConsentLocked.IsVisible = !_withoutPasswordAllowed;
        Update();
        Opened += (_, _) => OwnerPassword.Focus();
    }

    /// <summary>Whether removing is allowed (the permissions password is correct or the confirmation is checked).</summary>
    internal bool CanLift => _ownerOk || _withoutPasswordAllowed && ConsentBox.IsChecked == true;

    /// <summary>Ticking the confirmation: asked once more, ticked only after „Ich bin berechtigt“; clearing it asks nothing.</summary>
    internal async Task OnConsentClicked()
    {
        if (ConsentBox.IsChecked != true) return;
        ConsentBox.IsChecked = false;
        if (_withoutPasswordAllowed && await ConfirmEntitled()) ConsentBox.IsChecked = true;
    }

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
        ConsentBox.IsEnabled = !_ownerOk && _withoutPasswordAllowed;
        LiftButton.IsEnabled = CanLift;
    }

    private void OnLift(object? sender, RoutedEventArgs e)
    {
        if (!CanLift) return;
        Close(new RestrictionsReply(KeepOpenPassword: RemoveOpenBox.IsChecked != true));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
