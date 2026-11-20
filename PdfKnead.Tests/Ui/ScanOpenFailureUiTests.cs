using PdfKnead.Core.Scanning;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Fehlergedächtnis 927 (found by the user): a scanner that cannot be opened (a network scanner in another network) was opened again and
/// again — the failed open searched the list, the search chose the same scanner, and it was opened anew; meanwhile the window was busy and
/// could not search. Now: one search after the failure, no opening by itself afterwards; the search is possible while a scanner is being
/// opened and stops the opening; „Scanner suchen“ and the user's choice try again.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class ScanOpenFailureUiTests
{
    /// <summary>One scanner that never opens: at once with „not reachable“, or it hangs until the opening is cancelled.</summary>
    private sealed class Unreachable(bool hang) : IScanBackend
    {
        public static readonly ScanDevice Device = new("weg:1", "Beispiel", "Netzscanner 9000", "network scanner");
        public int Opens;
        public int Searches;
        public int Cancelled;

        public string Name { get; } = "Unerreichbar-" + Guid.NewGuid().ToString("N");

        public bool IsAvailable(out string? hint)
        {
            hint = null;
            return true;
        }

        public Task<IReadOnlyList<ScanDevice>> FindDevicesAsync(CancellationToken cancel = default)
        {
            Searches++;
            return Task.FromResult<IReadOnlyList<ScanDevice>>([Device]);
        }

        public async Task<IScanSession> OpenAsync(ScanDevice device, CancellationToken cancel = default)
        {
            Opens++;
            if (!hang) throw new ScanException("Der Scanner antwortet nicht.", ScanErrorKind.Offline);
            try
            {
                await Task.Delay(Timeout.Infinite, cancel);
            }
            catch (OperationCanceledException)
            {
                Cancelled++;
                throw;
            }
            throw new InvalidOperationException("nie");
        }
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 20; i++) await Task.Delay(25);
    }

    [Fact]
    public void FailedOpen_OneSearch_NoOpeningByItself_SearchTriesAgain()
    {
        var backend = new Unreachable(hang: false);
        UiSession.Run(async () =>
        {
            var scan = new ScanViewModel(backend, canAppend: false);
            await scan.StartAsync();
            await Settle();
            Assert.Equal(1, backend.Opens);
            Assert.True(backend.Searches <= 2, $"{backend.Searches} Suchen");   // the first, and at most one after the failure
            var searches = backend.Searches;
            await Settle();
            Assert.Equal(1, backend.Opens);                                         // no loop
            Assert.Equal(searches, backend.Searches);
            Assert.False(scan.IsBusy);
            Assert.True(scan.SearchCommand.CanExecute(null));
            // „Scanner suchen“: tried once more, on request
            await scan.SearchAsync();
            await Settle();
            Assert.Equal(2, backend.Opens);
            Assert.False(scan.ScanCommand.CanExecute(null));
        });
    }

    [Fact]
    public void Opening_SearchStaysPossible_AndStopsTheOpening()
    {
        var backend = new Unreachable(hang: true);
        UiSession.Run(async () =>
        {
            var scan = new ScanViewModel(backend, canAppend: false);
            await scan.StartAsync();
            await Settle();
            Assert.True(scan.IsOpening);
            Assert.False(scan.IsBusy);
            Assert.True(scan.SearchCommand.CanExecute(null));                       // before: busy while opening, no search
            var search = scan.SearchAsync();
            await Settle();
            Assert.True(backend.Cancelled >= 1);                                     // the hanging opening was stopped
            scan.SelectedDevice = null;                                              // the window closes: no opening left behind
            await search;
            await Settle();
            Assert.False(scan.IsOpening);
        });
    }
}
