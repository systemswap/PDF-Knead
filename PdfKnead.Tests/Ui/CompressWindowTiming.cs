using System.Diagnostics;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Measurement (not part of the normal test run; enable with PDFKNEAD_MESSEN=1, use a release build): the compression window with
/// 40 photo pages under rapid changes (ten slider moves 0.35 s apart, each starting a trial run), then waiting for the final result.
/// Reports the time, the process peak and the allocated bytes.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class CompressWindowTiming
{
    [Fact]
    public void RapidChanges()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") is null) return;
        var pdf = TestFiles.Write(CompressTiming.Photos(40), "fotos-40.pdf");
        UiSession.Run(async () =>
        {
            Services.UserSettings.Update(s => s with { Compression = null, CompressWindow = null });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                CompressViewModel? compress = null;
                CompressWindow? dialog = null;
                vm.ShowCompress = c =>
                {
                    compress = c;
                    dialog = new CompressWindow(c);
                    _ = dialog.ShowDialog<string?>(window);
                    return Task.FromResult<string?>(null);
                };
                await vm.CompressCommand.ExecuteAsync(null);
                var c = compress!;
                await Until(() => c.IsReady, 300);

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                var start = process.WorkingSet64;
                long peak = start;
                var done = false;
                var sampler = new Thread(() =>
                {
                    using var p = Process.GetCurrentProcess();
                    while (!Volatile.Read(ref done))
                    {
                        p.Refresh();
                        peak = Math.Max(peak, p.WorkingSet64);
                        Thread.Sleep(10);
                    }
                }) { IsBackground = true };
                sampler.Start();
                var allocated = GC.GetTotalAllocatedBytes(true);
                var watch = Stopwatch.StartNew();
                var last = c.After;
                for (var i = 0; i < 10; i++)
                {
                    c.Quality = 50 + i * 3;
                    await Task.Delay(350);
                }
                await Until(() => c.IsReady && !ReferenceEquals(c.After, last), 600);
                watch.Stop();
                Volatile.Write(ref done, true);
                sampler.Join();
                Console.WriteLine($"MESS 10 schnelle Reglerbewegungen, 40 Fotoseiten: {watch.ElapsedMilliseconds} ms bis fertig, " +
                                  $"Spitze +{(peak - start) / 1e6:0} MB, belegt {(GC.GetTotalAllocatedBytes(true) - allocated) / 1e6:0} MB");
                dialog!.Close();
                await c.Released;
            }
            finally
            {
                Services.UserSettings.Update(s => s with { Compression = null, CompressWindow = null });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    private static async Task Until(Func<bool> condition, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(condition(), "Zeitüberschreitung");
    }
}
