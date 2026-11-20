using System.Diagnostics;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;
using Xunit.Abstractions;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Measurement (not part of the normal test run): „Suchen und schwärzen“ on a page with very many hits (KONZEPT.md, section 5.6) — a page of
/// 1 000 to 16 000 dates in tiny print, searched with the date pattern; the time should grow with the number of hits, not with its square.
/// Enable with PDFKNEAD_MESSEN=suchen, use a release build, and show the output with --logger "console;verbosity=detailed" (lines starting
/// with "MESS").
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class SearchRedactTiming(ITestOutputHelper output)
{
    [Fact]
    public void PageWithManyHits()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") != "suchen") return;
        UiSession.Run(async () =>
        {
            foreach (var count in new[] { 1_000, 4_000, 16_000 })
            {
                // 100 dates per line in 1-point print, the lines 1.2 points apart
                var lines = Enumerable.Range(0, count / 100).Select(row =>
                    $"BT /F1 1 Tf 10 {830 - row * 1.2:0.0} Td ({string.Join(" ", Enumerable.Range(0, 100).Select(k => $"{1 + k % 28}.{1 + row % 12}.2026"))}) Tj ET");
                var file = TestFiles.Write(FakeRedactionTests.Page(string.Join(" ", lines)), $"viele-treffer-{count}.pdf");
                var vm = new MainViewModel();
                var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
                window.Show();
                try
                {
                    await vm.OpenAsync([file]);
                    var watch = Stopwatch.StartNew();
                    var hits = await vm.SelectedDocument!.FindForRedactionAsync([], RedactionPatterns.Date, false);
                    output.WriteLine($"MESS {count} Datumsangaben: {hits.Count} Treffer, {watch.ElapsedMilliseconds} ms");
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }
}
