using Avalonia;

namespace FunktionManager;

internal static class Program
{
    /// <summary>Errors that end the program: written beside it, since a window program has no console to show them.</summary>
    private static string ErrorLog => Path.Combine(AppContext.BaseDirectory, "funktionmanager-fehler.txt");

    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteError(e.ExceptionObject as Exception);
        try
        {
            return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteError(ex);
            return 1;
        }
    }

    private static void WriteError(Exception? ex)
    {
        try
        {
            File.AppendAllText(ErrorLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // nowhere left to report it
        }
        catch (UnauthorizedAccessException)
        {
            // the folder is read-only
        }
    }
}
