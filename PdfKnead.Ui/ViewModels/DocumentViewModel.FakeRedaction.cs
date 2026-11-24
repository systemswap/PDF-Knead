using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Rendering;

namespace PdfKnead.ViewModels;

/// <summary>A place in the file where text only looks redacted.</summary>
/// <param name="Page">Page of the file (zero-based).</param>
/// <param name="Spot">What and where.</param>
public sealed record FakeRedactionHit(int Page, FakeRedactionSpot Spot);

/// <summary>
/// Redactions that are none (KONZEPT.md, section 5.6, „Vorgetäuschte Schwärzung“): checked in the background after opening and saving; if the
/// file has text that only looks blacked out — under a dark area, or under a redaction mark that was never applied —, a bar over the document
/// warns and offers to show it and to redact it properly (marks over the places, applied when saving as every redaction).
/// </summary>
public partial class DocumentViewModel
{
    private CancellationTokenSource? _fakeCheck;
    private int _fakeGeneration;

    /// <summary>The places found; empty if there are none (or the check is still running).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FakeRedactionWarningVisible), nameof(FakeRedactionText))]
    public partial IReadOnlyList<FakeRedactionHit> FakeRedactions { get; private set; } = [];

    /// <summary>The user closed the bar (it comes back after the next check that finds something).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FakeRedactionWarningVisible))]
    public partial bool FakeRedactionDismissed { get; private set; }

    /// <summary>Whether the warning bar shows.</summary>
    public bool FakeRedactionWarningVisible => FakeRedactions.Count > 0 && !FakeRedactionDismissed;

    /// <summary>„Achtung: Auf Seite 2 sieht Text geschwärzt aus …“.</summary>
    public string FakeRedactionText
    {
        get
        {
            var pages = FakeRedactions.Select(h => h.Page + 1).Distinct().Order().ToList();
            var list = string.Join(", ", pages.Take(8)) + (pages.Count > 8 ? " …" : "");
            return pages.Count == 1
                ? Tr.T("Achtung: Auf Seite {0} sieht Text geschwärzt aus, steht aber noch in der Datei — jeder kann ihn herauskopieren.", list)
                : Tr.T("Achtung: Auf den Seiten {0} sieht Text geschwärzt aus, steht aber noch in der Datei — jeder kann ihn herauskopieren.", list);
        }
    }

    /// <summary>
    /// Looks for text that only looks redacted, in the background (the file is opened once more for it, as for the signatures); the result
    /// replaces <see cref="FakeRedactions"/>.
    /// </summary>
    /// <returns>A task that completes when the result is there (tests wait for it).</returns>
    public Task CheckFakeRedactionsAsync()
    {
        _fakeCheck?.Cancel();
        var generation = ++_fakeGeneration;
        if (IsUntitled)
        {
            FakeRedactions = [];
            return Task.CompletedTask;
        }
        var cancel = _fakeCheck = new CancellationTokenSource();
        var (path, password) = (Path, _password);
        return Run();

        async Task Run()
        {
            List<FakeRedactionHit> hits;
            try
            {
                hits = await Task.Run(() =>
                {
                    using var file = PdfDocument.Open(path, password);
                    var images = new ImageCache(64L << 20);
                    var found = new List<FakeRedactionHit>();
                    for (var i = 0; i < file.PageCount && found.Count < 1000; i++)
                    {
                        try
                        {
                            found.AddRange(PdfKnead.Rendering.FakeRedactions.Find(file, file.Pages[i], images, cancel.Token).Select(s => new FakeRedactionHit(i, s)));
                        }
                        catch (Exception e) when (e is PdfException or InvalidDataException)
                        {
                            // a damaged page: the others are checked
                        }
                    }
                    return found;
                }, cancel.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PdfException or InvalidDataException)
            {
                hits = [];
            }
            if (generation != _fakeGeneration || _closed) return;
            FakeRedactionDismissed = false;
            FakeRedactions = hits;
        }
    }

    /// <summary>„Anzeigen“: goes to the first place.</summary>
    [RelayCommand]
    private void ShowFakeRedaction()
    {
        if (FakeRedactions.FirstOrDefault() is { } first && PositionOf(first.Page) is { } position) ScrollToPage(position);
    }

    /// <summary>
    /// „Richtig schwärzen“: puts a redaction mark over every place (one undo step); applying them („Schwärzungen anwenden“, or saving as a
    /// new file) removes the text as with every mark. Places a mark of the program already covers are left as they are.
    /// </summary>
    [RelayCommand]
    private void MarkFakeRedactions()
    {
        if (!CanRedact)
        {
            Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return;
        }
        var pages = History.Pages.ToArray();
        var added = 0;
        foreach (var hit in FakeRedactions)
        {
            if (PositionOf(hit.Page) is not { } position || position >= pages.Length) continue;
            var mark = RedactionElement.FromPageAreas(hit.Spot.Areas);
            if (pages[position].Elements.OfType<RedactionElement>().Any(e => e.PageAreas.SequenceEqual(mark.PageAreas))) continue;
            pages[position] = pages[position].WithElements([.. pages[position].Elements, mark]);
            added++;
        }
        if (added > 0) History.Apply(Tr.T("Vorgetäuschte Schwärzung richtig schwärzen"), pages);
        FakeRedactionDismissed = true;
        Notify(added == 1
            ? Tr.T("1 Stelle zum Schwärzen markiert — mit „Schwärzungen anwenden“ wird der Text darunter wirklich entfernt.")
            : Tr.T("{0} Stellen zum Schwärzen markiert — mit „Schwärzungen anwenden“ wird der Text darunter wirklich entfernt.", added));
    }

    /// <summary>Closes the bar for this check.</summary>
    [RelayCommand]
    private void DismissFakeRedaction() => FakeRedactionDismissed = true;
}
