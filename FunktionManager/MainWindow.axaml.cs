using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FunktionManager;

/// <summary>
/// The FunktionManager: writes the function file of an organization (berechtigungen.json beside PDF-Knead) — which functions its users may
/// use, the kind of use, the name of the organization. Nothing of license keys; nothing is sent.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Editor.Said += Say;
        Opened += (_, _) => FitToScreen();
        Say("Häkchen setzen, „Funktionsdatei speichern …“, die Datei neben PDF-Knead legen.");
        // a function file beside the FunktionManager (and so beside PDF-Knead) is read at once
        Editor.LoadBeside(PolicyEditor.ProgramFolder);
    }

    private void Say(string message, bool warn = false)
    {
        StatusText.Text = warn ? "⚠ " + message : message;
        StatusText.Classes.Set("warn", warn);
        ToolTip.SetTip(StatusText, StatusText.Text);
    }

    /// <summary>On a small screen the window is not taller than the screen; the content scrolls.</summary>
    private void FitToScreen()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen) return;
        var available = screen.WorkingArea.Height / screen.Scaling - 50;
        if (Bounds.Height <= available) return;
        SizeToContent = SizeToContent.Manual;
        Height = available;
        Position = new PixelPoint(Position.X, screen.WorkingArea.Y + 8);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
