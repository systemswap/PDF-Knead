using System.Diagnostics;
using PdfKnead.Core.Platform;

namespace PdfKnead.Core.Updates;

/// <summary>
/// The exchange at the start (KONZEPT.md §5.26). A running program cannot be overwritten under Windows, so the exchange happens between two
/// processes: the old program starts the new one from the waiting area and ends; the new one waits for it, exchanges the files and starts
/// the program in its folder. A version that does not reach its main window is put back the same way, by the previous version.
/// </summary>
public static class UpdateStart
{
    /// <summary>The argument with which the new program from the waiting area exchanges the files: folder, process, version.</summary>
    public const string ApplyArgument = "--update-anwenden";

    /// <summary>The argument with which the previous version puts itself back: folder, process.</summary>
    public const string RestoreArgument = "--update-zurueck";

    /// <summary>
    /// Set for the program started after an exchange or a restore: this start does not hand over again — an exchange that failed (another
    /// window of the program held its files) is tried at the next start, not in a loop.
    /// </summary>
    public const string AfterExchange = "PDFKNEAD_NACH_UPDATE";

    /// <summary>Where the waiting area, the previous version and the notes of the exchange lie.</summary>
    public static string StateFolder => Path.Combine(AppLocations.UserDataFolder, "updates");

    /// <summary>
    /// The folder of the program and the file to start: next to the program, or for an AppImage the folder and file of the AppImage (the
    /// program itself runs from a mounted copy).
    /// </summary>
    public static (string Folder, string Executable) ProgramLocation()
    {
        if (Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } image && File.Exists(image))
            return (Path.GetDirectoryName(image)!, image);
        var exe = Environment.ProcessPath ?? Path.Combine(AppLocations.ProgramFolder, "PDF-Knead");
        return (Path.GetDirectoryName(exe)!, exe);
    }

    /// <summary>
    /// Runs before anything else in Main. Returns true when this process has done its part and must end: it exchanged the files (started from
    /// the waiting area), put the previous version back, or handed over to the new version.
    /// </summary>
    /// <param name="args">The arguments of the program.</param>
    /// <param name="installer">The waiting area; the program's if null.</param>
    /// <param name="start">Starts a process; the system's if null (tests record instead).</param>
    /// <param name="location">The program folder and file; <see cref="ProgramLocation"/> if null (tests: a folder of their own).</param>
    public static bool Run(string[] args, UpdateInstaller? installer = null, Func<ProcessStartInfo, bool>? start = null,
        (string Folder, string Executable)? location = null)
    {
        installer ??= new UpdateInstaller(StateFolder);
        start ??= info => Process.Start(info) is not null;
        var exe = Path.GetFileName((location ?? ProgramLocation()).Executable);
        try
        {
            if (args is [ApplyArgument, var folder, var pidText, var versionText])
            {
                WaitForExit(pidText);
                if (UpdateVersion.Parse(versionText) is { } version && installer.Waiting() is { } waiting && waiting.Version.CompareTo(version) == 0)
                    installer.Apply(folder, version, waiting.Files);
                start(Program(folder, exe));
                return true;
            }
            if (args is [RestoreArgument, var restoreFolder, var restorePid])
            {
                WaitForExit(restorePid);
                installer.CheckStart(restoreFolder);
                start(Program(restoreFolder, exe));
                return true;
            }
            if (Environment.GetEnvironmentVariable(AfterExchange) == "1")
            {
                installer.CountStart();
                return false;
            }
            var (programFolder, _) = location ?? ProgramLocation();
            // the exchanged version failed at its first start: the previous version puts itself back from its own folder
            if (installer.NeedsRestore() && Path.Combine(installer.Previous, exe) is var previous && File.Exists(previous))
            {
                AppLog.Write("Update", "die neue Fassung erreichte ihr Fenster nicht — die Vorversion wird zurückgeholt");
                return start(new ProcessStartInfo(previous, [RestoreArgument, programFolder, Environment.ProcessId.ToString()]) { UseShellExecute = false });
            }
            installer.CountStart();
            // a version waits: hand over to it, if the program may replace its own files and the waiting one is newer
            if (installer.Waiting() is { } ready && UpdateVersion.Parse(AppVersion.Raw) is { } current)
            {
                if (!ready.Version.IsNewerThan(current))
                {
                    installer.Discard(ready.Version);
                    return false;
                }
                return HandOver(installer, start, location);
            }
            installer.CleanUp();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("Update", e.Message);   // the program starts as it is
        }
        return false;
    }

    /// <summary>
    /// Starts the new program from the waiting area to exchange the files once this process has ended („Jetzt neu starten“, and at the start).
    /// </summary>
    /// <returns>Whether it was started; then this process must end.</returns>
    public static bool HandOver(UpdateInstaller? installer = null, Func<ProcessStartInfo, bool>? start = null,
        (string Folder, string Executable)? location = null)
    {
        installer ??= new UpdateInstaller(StateFolder);
        start ??= info => Process.Start(info) is not null;
        if (installer.Waiting() is not { } ready) return false;
        var (programFolder, executable) = location ?? ProgramLocation();
        var helper = Path.Combine(installer.Staged(ready.Version), Path.GetFileName(executable));
        if (!UpdateInstaller.CanWrite(programFolder) || !File.Exists(helper)) return false;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(helper, File.GetUnixFileMode(helper) | UnixFileMode.UserExecute);
        AppLog.Write("Update", $"übergibt an {ready.Version}");
        return start(new ProcessStartInfo(helper, [ApplyArgument, programFolder, Environment.ProcessId.ToString(), ready.Version.ToString()])
            { UseShellExecute = false });
    }

    /// <summary>The program in its folder, started after an exchange or a restore (without handing over again).</summary>
    private static ProcessStartInfo Program(string folder, string exe)
    {
        var info = new ProcessStartInfo(Path.Combine(folder, exe)) { UseShellExecute = false, WorkingDirectory = folder };
        info.Environment[AfterExchange] = "1";
        return info;
    }

    /// <summary>The name of the program file: „PDF-Knead.exe“, or the name of the AppImage.</summary>
    public static string ExecutableName() => Path.GetFileName(ProgramLocation().Executable);

    private static void WaitForExit(string pidText)
    {
        if (!int.TryParse(pidText, out var pid)) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            process.WaitForExit(TimeSpan.FromSeconds(60));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already ended, or not ours to wait for
        }
    }
}
