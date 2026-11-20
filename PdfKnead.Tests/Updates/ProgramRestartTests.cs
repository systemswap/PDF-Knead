using System.Diagnostics;
using PdfKnead.Core.Updates;

namespace PdfKnead.Tests.Updates;

/// <summary>The program started anew with the files that were open; the new start waits for the old one.</summary>
public sealed class ProgramRestartTests
{
    [Fact]
    public void Start_PassesTheExistingFiles_AndWhomToWaitFor()
    {
        var folder = Directory.CreateTempSubdirectory("pdfknead-neustart-").FullName;
        try
        {
            var open = Path.Combine(folder, "offen.pdf");
            File.WriteAllText(open, "%PDF-1.7");
            ProcessStartInfo? started = null;
            Assert.True(ProgramRestart.Start([open, open, Path.Combine(folder, "weg.pdf")], info =>
            {
                started = info;
                return true;
            }));
            Assert.NotNull(started);
            Assert.Equal([open], started!.ArgumentList);
            Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), started.Environment[ProgramRestart.WaitVariable]);
            Assert.False(started.UseShellExecute);
            Assert.False(ProgramRestart.Start([], _ => false));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void WaitForPrevious_EndsAtOnce_WhenThePreviousIsGone()
    {
        Environment.SetEnvironmentVariable(ProgramRestart.WaitVariable, "999999999");
        var watch = Stopwatch.StartNew();
        ProgramRestart.WaitForPrevious();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Null(Environment.GetEnvironmentVariable(ProgramRestart.WaitVariable));
    }
}
