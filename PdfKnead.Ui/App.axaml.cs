using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using PdfKnead.Core.Platform;
using PdfKnead.Services;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead;

public partial class App : Application
{
    public override void Initialize()
    {
        // Tooltips above the element instead of at the mouse pointer, so that they do not cover the
        // next button.
        AppLog.StartStep("Avalonia bereit, App.Initialize");
        TooltipAbove<TemplatedControl>();
        TooltipAbove<TextBlock>();
        TooltipAbove<Border>();
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    private static void TooltipAbove<T>() where T : Control
    {
        ToolTip.PlacementProperty.OverrideDefaultValue<T>(PlacementMode.Top);
        ToolTip.VerticalOffsetProperty.OverrideDefaultValue<T>(-6);
    }

    /// <summary>
    /// After the main window opened: the window of the first start (language, own certificate, updates — once), then the automatic check for updates if switched on (at most once a day, in the background). Only here, in the
    /// real program — never from the tests.
    /// </summary>
    private static async Task FirstStartAsync(MainViewModel vm)
    {
        Services.SupportPrompt.NoteStart(DateOnly.FromDateTime(DateTime.Now));
        await vm.OfferWelcomeAsync();
        await Services.Updates.Instance.CheckAtStartAsync();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Settings, language and appearance before the first window.
            AppLog.StartStep("Stile geladen");
            UserSettings.Load();
            // the function file of the organization beside the program (KONZEPT §5.27): once, before anything asks for the kind of use
            Core.Policy.ActivePolicy.Use(Core.Policy.ActivePolicy.LoadBesideProgram());
            // Remove leftovers of earlier runs (temporary files, old versions such as an unredacted original) in the background.
            PdfKnead.Core.Pdf.Writing.PdfFileSaver.LeftoverList = Path.Combine(Path.GetDirectoryName(UserSettings.File)!, "aufraeumen.txt");
            _ = Task.Run(PdfKnead.Core.Pdf.Writing.PdfFileSaver.DeleteLeftoversOfEarlierRuns);
            UserSettings.ApplyLanguage();
            UserSettings.ApplyTheme();
            AppLog.StartStep("Einstellungen");

            var vm = new MainViewModel();
            AppLog.StartStep("MainViewModel");
            // Last safety net: an error in a command (e.g. caused by a crafted file) does not terminate the whole
            // program with all open documents; a message is shown and the details go to the log.
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                AppLog.Write("Unerwarteter Fehler (abgefangen, Programm läuft weiter)", e.Exception.ToString());
                e.Handled = true;
                vm.Message = Core.Localization.Tr.T("Unerwarteter Fehler — Einzelheiten stehen im Log.");
            };
            var window = new MainWindow { DataContext = vm };
            AppLog.StartStep("MainWindow gebaut");
            window.Opened += (_, _) =>
            {
                AppLog.StartStep("Fenster offen");
                // First rendered frame
                TopLevel.GetTopLevel(window)?.RequestAnimationFrame(_ => AppLog.StartStep("erstes Bild", done: true));
                // Updates (KONZEPT §5.26): the main window opened, so an exchanged version works
                Services.Updates.Instance.ConfirmStart();
                _ = FirstStartAsync(vm);
            };
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => GuardExit();

            // „Dateien öffnen: im zuletzt benutzten Fenster“ (decided on 16.11.2026): files a new start hands over open here as new
            // tabs, the window comes to the front (restored as it was before it was minimised). Listening always, so that switching the
            // setting works at once; only this user can connect (FileHandoff).
            var shownState = window.WindowState;
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WindowStateProperty && window.WindowState != WindowState.Minimized) shownState = window.WindowState;
            };
            var handoff = FileHandoff.Listen(paths => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _ = vm.OpenAsync(paths);
                if (window.WindowState == WindowState.Minimized) window.WindowState = shownState;
                window.Activate();
            }));
            window.Activated += (_, _) => FileHandoff.MarkUsed();
            desktop.Exit += (_, _) =>
            {
                handoff.Dispose();
                FileHandoff.Unmark();
            };

            // Files from “Open with” or from the .desktop line (Exec=… %F).
            if (desktop.Args is { Length: > 0 } args)
                _ = vm.OpenAsync(args);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Window closed, program ending: if shutting down hangs (a thread, a library), the process would live on invisibly, and on Linux
    /// the mounted AppImage (/tmp/.mount_…) would remain (observed once). After 10 s, writes what was still running to the log
    /// and terminates the process; by then everything has been saved (closing waits for running work).
    /// </summary>
    private static void GuardExit()
    {
        var guard = new Thread(() =>
        {
            Thread.Sleep(10_000);
            AppLog.Write("Beenden hing — Prozess nach 10 s selbst beendet",
                $"Threads: {System.Diagnostics.Process.GetCurrentProcess().Threads.Count}, Arbeit: {AppLog.CurrentActivity ?? "keine"}");
            AppLog.Stop();   // otherwise the next start would report a crash (Main no longer reaches its finally block)
            Environment.Exit(0);
        }) { IsBackground = true, Name = "Beenden absichern" };
        guard.Start();
    }
}
