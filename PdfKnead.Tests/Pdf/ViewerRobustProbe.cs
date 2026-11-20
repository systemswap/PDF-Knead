using System.Diagnostics;
using System.Text;
using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Probe run of the viewer analysis (not part of the normal test run; enabled with PDFKNEAD_PRUEFUNG=robust): re-checks the
/// review findings with crafted data streams, measuring time and memory per case under a time limit. Writes the report to
/// %TEMP%\pdf-knead-robust.txt.
/// </summary>
public sealed class ViewerRobustProbe
{
    private static byte[] Codestream(uint x0, uint x1, uint tile, int components, byte dx, byte levels, ushort layers, byte order, int body = 1) =>
        JpxRobustTests.Codestream(x0, x1, tile, components, dx, levels, layers, order, body);

    private static string Measure(Action action, int seconds = 20)
    {
        var before = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        var task = Task.Run(() =>
        {
            try
            {
                action();
                return "ok";
            }
            catch (Exception e)
            {
                return e.GetType().Name + ": " + e.Message;
            }
        });
        var done = task.Wait(TimeSpan.FromSeconds(seconds));
        var allocated = (GC.GetTotalAllocatedBytes(true) - before) >> 20;
        return done ? $"{task.Result,-28} {watch.ElapsedMilliseconds,7} ms {allocated,6} MB belegt" : $"HÄNGT (> {seconds} s)            {allocated,6} MB belegt";
    }

    [Fact]
    public void Report()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_PRUEFUNG") != "robust") return;
        var report = new StringBuilder();
        // R1: tile components without precincts, many layers and components
        report.AppendLine("R1 JPX Paketschleife ohne Bereiche:      " + Measure(() => JpxDecoder.Decode(Codestream(1, 2, 2, 2000, 255, 32, 65535, 0), 16_000_000)));
        report.AppendLine("R1 mit 1 Schicht:                        " + Measure(() => JpxDecoder.Decode(Codestream(1, 2, 2, 2000, 255, 32, 1, 0), 16_000_000)));
        report.AppendLine("R1 mit 10 Komponenten:                   " + Measure(() => JpxDecoder.Decode(Codestream(1, 2, 2, 10, 255, 32, 65535, 0), 16_000_000)));
        // R2/R3 (Assemble): very wide image, many components
        report.AppendLine("R2 breit, 16000 Komp., dx 255:           " + Measure(() => JpxDecoder.Decode(Codestream(0, 1_000_000, 1_000_000, 16000, 255, 0, 1, 0), 16_000_000)));
        report.AppendLine("R2 breit, 8000 Komp., dx 255:            " + Measure(() => JpxDecoder.Decode(Codestream(0, 1_000_000, 1_000_000, 8000, 255, 0, 1, 0), 16_000_000)));
        
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pdf-knead-robust.txt"), report.ToString());
    }
}
