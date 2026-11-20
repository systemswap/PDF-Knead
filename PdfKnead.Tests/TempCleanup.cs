using System.Runtime.CompilerServices;

namespace PdfKnead.Tests;

/// <summary>
/// Removes temporary folders of earlier test runs. The tests create folders under %TEMP% (pdf-knead-test-…, -ui-, -settings- …) and
/// do not delete them individually; without this cleanup, 35 000 folders with 13 GB accumulated. Only folders older than one hour are
/// removed, so that a test run in progress at the same time keeps its own. The cleanup runs in the background; start-up does not wait for it.
/// </summary>
internal static class TempCleanup
{
    [ModuleInitializer]
    internal static void RemoveOldRuns() => Task.Run(() =>
    {
        var before = DateTime.UtcNow.AddHours(-1);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "pdf-knead-*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < before) Directory.Delete(dir, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // still in use or already gone: retried on the next run
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    });
}
