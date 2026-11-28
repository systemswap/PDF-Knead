using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PdfKnead.Services;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The settings file, the device key and the redaction codes of the test process lie in a folder under %TEMP% from the moment the test
/// assembly is loaded, never in the user profile. Before, the folder was set only when the first test started the UI session: a test that read
/// or changed <see cref="UserSettings"/> before its first <see cref="UiSession.Run(Action)"/> — run alone, or first in a run — read and wrote the
/// settings file of the user profile, and the session then went on with those settings. With time stamps switched on there (a service with a
/// login), saving a digital signature asked for the password in a window nobody answers: the test hung, only when run alone.
/// </summary>
internal static class TestProfile
{
    [ModuleInitializer]
    internal static void UseTemporaryFolder()
    {
        var dir = Directory.CreateTempSubdirectory("pdf-knead-ui-").FullName;
        // the whole per-user data folder first: the own certificate, trust lists, device code, window marker, update state and signature fonts
        // lie there too (No. 1240)
        Core.Platform.AppLocations.UserDataFolderForTests = Path.Combine(dir, "benutzerdaten");
        UserSettings.File = Path.Combine(dir, "settings.json");
        Device.File = Path.Combine(dir, "geraet.key");
        Core.Pdf.Editing.RedactionCodes.Folder = Path.Combine(dir, "Schwaerzungscodes");   // never the user's own code files
    }
}

/// <summary>
/// Headless Avalonia session with real rendering through Skia, so that windows can be
/// checked and saved as images. One session serves all UI tests (Avalonia can start only
/// once per process). Settings are stored in a folder under %TEMP%, never in the
/// user profile (<see cref="TestProfile"/>).
/// </summary>
public static class UiSession
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() =>
        HeadlessUnitTestSession.StartNew(typeof(UiSession), AvaloniaTestIsolationLevel.PerAssembly));

    /// <summary>Builds the Avalonia application; called by <see cref="HeadlessUnitTestSession"/> through reflection.</summary>
    /// <returns>The configured application builder.</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();

    /// <summary>Runs an action on the UI thread and waits for it.</summary>
    /// <param name="action">The action to run.</param>
    public static void Run(Action action) => Session.Value.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Runs a function on the UI thread and waits for its result.</summary>
    /// <param name="func">The function to run.</param>
    /// <returns>The result of <paramref name="func"/>.</returns>
    public static T Run<T>(Func<T> func) => Session.Value.Dispatch(func, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Runs an asynchronous action on the UI thread and waits for it (opening a document runs in the background and returns to the UI thread).</summary>
    /// <param name="action">The asynchronous action to run.</param>
    public static void Run(Func<Task> action) =>
        Session.Value.Dispatch(async () =>
        {
            await action();
            return 0;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Completes layout, bindings and rendering, then captures the rendered frame.</summary>
    /// <param name="top">The window or top level to capture.</param>
    /// <returns>The rendered frame.</returns>
    public static WriteableBitmap Capture(TopLevel top)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return top.CaptureRenderedFrame() ?? throw new InvalidOperationException("Kein Bild gezeichnet");
    }

    /// <summary>
    /// Returns the folder for test screenshots and creates it, but only if PDFKNEAD_SCREENSHOTS is set
    /// (a path, or "1" for %LOCALAPPDATA%/pdf-knead-build/screenshots).
    /// </summary>
    /// <returns>The folder path, or null if PDFKNEAD_SCREENSHOTS is not set.</returns>
    public static string? ScreenshotFolder()
    {
        var value = Environment.GetEnvironmentVariable("PDFKNEAD_SCREENSHOTS");
        if (string.IsNullOrEmpty(value)) return null;
        var folder = value == "1"
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pdf-knead-build", "screenshots")
            : value;
        Directory.CreateDirectory(folder);
        return folder;
    }
}
