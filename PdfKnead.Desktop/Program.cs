using Avalonia;
using PdfKnead.Core.Platform;

namespace PdfKnead.Desktop;

sealed class Program
{
    // No Avalonia and nothing that needs a SynchronizationContext before AppMain.
    [STAThread]
    public static void Main(string[] args)
    {
        // Started as scan helper: only the scanner interface (WIA under Windows, SANE elsewhere), no window, no log of its own (KONZEPT §5.25)
        if (args is [PdfKnead.Core.Scanning.Helper.ScanHelperHost.Argument, var toHelper, var fromHelper])
        {
            Environment.Exit(PdfKnead.Core.Scanning.Helper.ScanHelperHost.Run(toHelper, fromHelper, PdfKnead.Core.Scanning.ScanBackends.Direct()));
            return;
        }
        // started anew by the program itself (a function file was installed): first let the previous run end, so that its files are not
        // handed over to the window that is just closing
        PdfKnead.Core.Updates.ProgramRestart.WaitForPrevious();
        // „Dateien öffnen: im zuletzt benutzten Fenster“: the files go to a window already open and this start ends — no log, no window.
        // Without such a window (or with anything but files) the start goes on as usual.
        if (args.Length > 0 && AppSettings.Load().OpenInRunningWindow && FileHandoff.TryHandOver(args, TimeSpan.FromSeconds(2)))
            return;
        PdfKnead.Core.Scanning.ScanBackends.HelperExecutable = Environment.ProcessPath;
        // The program's own log next to the program (crashes, including those of the previous run).
        AppLog.Start();
        AppLog.StartStep("Main");
        // updates (KONZEPT §5.26): exchange the files as the new program from the waiting area, put a failed version back, or hand over to
        // a waiting version — then this process ends here, before any window
        if (PdfKnead.Core.Updates.UpdateStart.Run(args))
        {
            AppLog.Stop();
            return;
        }
        try
        {
            EmbeddedNativeLibraries.Load();
            AppLog.StartStep("native Bibliotheken");
            // hidden text („Verborgenes schwärzen“, sanitising) is measured by drawing
            PdfKnead.Core.Pdf.Editing.TextVisibility.DefaultProbe = PdfKnead.Rendering.VisibilityProbe.Instance;
            // PDF/A-1 allows no transparency: what is translucent is drawn as an image
            PdfKnead.Core.Pdf.Writing.PdfAFlattening.Rasterizer = PdfKnead.Rendering.AreaRasterizer.Instance;
            PdfKnead.EInvoice.Ui.EInvoiceModule.Register();   // Andockstelle E-Rechnung (KONZEPT §5.24): the tab „E-Rechnung“
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            AppLog.Stop();
        }
    }

    // Avalonia configuration; also used by the designer.
    // AVALONIA_X11_CSD: EnableDrawnDecorations is still marked as experimental in Avalonia 12.1.
#pragma warning disable AVALONIA_X11_CSD
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // Linux: the panel (GNOME, KDE …) assigns a window to its .desktop file via WM_CLASS.
            // Must match StartupWMClass in packaging/linux/PDF-Knead.desktop.
            // EnableDrawnDecorations: Avalonia draws the window frame itself, as on Windows,
            // so that the custom title bar (program on the left, file in the centre) also appears on Linux.
            // See docs/FEHLERGEDAECHTNIS.md, No. 26.
            .With(new X11PlatformOptions { WmClass = "PDF-Knead", EnableDrawnDecorations = true })
            .AfterPlatformServicesSetup(_ => AppLog.StartStep("Plattform eingerichtet"))
            .LogToTrace();

        // With PDFKNEAD_STARTZEIT=1 and PDFKNEAD_AVALONIA_LOG=1: Avalonia's messages between the start-up timings.
        if (Environment.GetEnvironmentVariable("PDFKNEAD_AVALONIA_LOG") == "1")
            builder = builder.LogToDelegate(message => AppLog.StartStep("Avalonia: " + message), Avalonia.Logging.LogEventLevel.Debug);

        // PDFKNEAD_GRAFIK=software: render without the graphics card (for driver problems).
        if (Environment.GetEnvironmentVariable("PDFKNEAD_GRAFIK") == "software")
            builder = builder
                .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
                .With(new X11PlatformOptions { WmClass = "PDF-Knead", EnableDrawnDecorations = true, RenderingMode = [X11RenderingMode.Software] });
        return builder;
    }
#pragma warning restore AVALONIA_X11_CSD
}
