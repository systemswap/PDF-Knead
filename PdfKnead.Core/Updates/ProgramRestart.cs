using System.Diagnostics;

namespace PdfKnead.Core.Updates;

/// <summary>
/// Starts the program anew with the files that were open (after a function file was installed: it is read at the start). The new process
/// waits until this one has ended — otherwise it could hand its files over to the window that is just closing („Dateien öffnen: im zuletzt
/// benutzten Fenster“) and they would be lost.
/// </summary>
public static class ProgramRestart
{
    /// <summary>The environment variable with the process id the new start waits for.</summary>
    public const string WaitVariable = "PDFKNEAD_NEUSTART_NACH";

    /// <summary>How long the new start waits at most for the old process.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    /// <summary>Starts the program with the files; true when the new process could be started.</summary>
    /// <param name="files">The files to open again (those that no longer exist are left out).</param>
    /// <param name="start">Starts a process; the system's if null (tests record instead).</param>
    public static bool Start(IEnumerable<string> files, Func<ProcessStartInfo, bool>? start = null)
    {
        start ??= info => Process.Start(info) is not null;
        var (folder, executable) = UpdateStart.ProgramLocation();
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = folder };
        foreach (var file in files.Where(File.Exists).Distinct(StringComparer.Ordinal)) info.ArgumentList.Add(file);
        info.Environment[WaitVariable] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            return start(info);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>At the very start: when started by <see cref="Start"/>, waits until the previous process has ended.</summary>
    public static void WaitForPrevious()
    {
        if (Environment.GetEnvironmentVariable(WaitVariable) is not { Length: > 0 } text) return;
        Environment.SetEnvironmentVariable(WaitVariable, null);
        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid)) return;
        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(MaxWait);
        }
        catch (ArgumentException)
        {
            // already ended
        }
        catch (InvalidOperationException)
        {
            // already ended
        }
    }
}
