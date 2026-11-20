using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.ViewModels;

/// <summary>What a run of the text recognition did.</summary>
/// <param name="Read">Pages that got a text layer.</param>
/// <param name="WithoutImage">Pages skipped because they show no image (their text is real text already).</param>
/// <param name="Empty">Pages read on which nothing was found.</param>
/// <param name="Cancelled">Stopped before the last page; what was read is kept.</param>
public sealed record RecognitionResult(int Read, int WithoutImage, int Empty, bool Cancelled);

/// <summary>
/// Text recognition in the open document (KONZEPT.md, section 5.9): the pages are read one after another in the background, then the text
/// layers go into the document as one step that Ctrl+Z undoes. Reading can be cancelled — what was read so far is kept; saving waits for it.
/// </summary>
public sealed partial class DocumentViewModel
{
    private CancellationTokenSource? _recognition;

    /// <summary>Progress of a running text recognition for the status bar („Texterkennung: Seite 3 von 14 …“); null when none runs.</summary>
    [ObservableProperty]
    public partial string? RecognitionStatus { get; set; }

    /// <summary>Whether a text recognition is running (it can be cancelled).</summary>
    public bool IsRecognizing => _recognition is not null;

    /// <summary>Stops the running text recognition; the pages read so far keep their text.</summary>
    [RelayCommand]
    private void CancelRecognition() => _recognition?.Cancel();

    /// <summary>Recognises the text of pages and puts it over them as an invisible layer, one undoable step.</summary>
    /// <param name="pages">The pages (indices); pages without an image are skipped.</param>
    /// <param name="open">Opens the recognition (loads the models), on the background thread.</param>
    /// <param name="description">The name of the step for undo.</param>
    /// <returns>What was done.</returns>
    public async Task<RecognitionResult> RecognizeTextAsync(IReadOnlyList<int> pages, Func<ITextRecognizer> open, string description)
    {
        using var cancel = new CancellationTokenSource();
        _recognition = cancel;
        OnPropertyChanged(nameof(IsRecognizing));
        int read = 0, withoutImage = 0, empty = 0;
        var cancelled = false;
        try
        {
            await WorkAsync(() =>
            {
                var items = History.Pages.ToArray();
                var main = Document;
                var password = _password;
                // the new layer must stay when saving: what was cleaned of invisible text before does not apply to it
                var options = SaveOptions(SaveVersion) with { Cleanup = Cleaned & ~CleanupItems.InvisibleText };
                var chosen = pages.Where(i => (uint)i < (uint)items.Length).Distinct().Order().ToList();
                return Task.Run(() =>
                {
                    var layers = new Dictionary<int, PdfStream>();
                    using (var recognizer = open())
                    {
                        var images = new ImageCache(64L << 20);
                        for (var k = 0; k < chosen.Count; k++)
                        {
                            var i = chosen[k];
                            Report(Tr.T("Texterkennung: Seite {0} ({1} von {2}) …", i + 1, k + 1, chosen.Count));
                            ReportPlace(i, []);
                            (IReadOnlyList<OcrLine> Lines, Core.Pdf.Graphics.RectD PageBox)? found;
                            try
                            {
                                found = PageRecognition.Recognize(items[i], recognizer, images, cancel.Token, reading: areas => ReportPlace(i, areas));
                            }
                            catch (OperationCanceledException)
                            {
                                cancelled = true;
                                break;
                            }
                            if (found is not var (lines, box)) withoutImage++;
                            else if (OcrTextLayer.Form(lines, box) is { } form)
                            {
                                layers[i] = form;
                                read++;
                            }
                            else empty++;
                            if (cancel.IsCancellationRequested && k + 1 < chosen.Count)
                            {
                                cancelled = true;
                                break;
                            }
                        }
                        images.Clear();
                    }
                    if (layers.Count == 0) return ((PdfDocument, PageItem[])?)null;
                    ReportPlace(null, []);
                    Report(Tr.T("Texterkennung: Textebene wird eingefügt …"));
                    return WithTextLayers(main, items, options, layers, password);
                }, CancellationToken.None);
            }, result => AdoptTextLayers(result, description));
        }
        finally
        {
            _recognition = null;
            RecognitionStatus = null;
            SetRecognitionPlace(null, []);
            RecognitionFollows = true;
            OnPropertyChanged(nameof(IsRecognizing));
        }
        return new RecognitionResult(read, withoutImage, empty, cancelled);
    }

    /// <summary>The document with new text layers on pages (an old text layer of these pages goes, the new one takes its place).</summary>
    private static (PdfDocument, PageItem[])? WithTextLayers(PdfDocument main, PageItem[] items, PdfSaveOptions options, Dictionary<int, PdfStream> layers,
        string? password) =>
        InDocumentRedaction.Apply(main, items, options with
        {
            TextLayers = layers,
            ScopedCleanup = new CleanupScope(CleanupItems.InvisibleText, layers.Keys.ToHashSet()),
        }, password, CancellationToken.None, redact: false);

    /// <summary>Takes the document with the new text layers as one step.</summary>
    private Task AdoptTextLayers((PdfDocument Doc, PageItem[] Adopted)? result, string description)
    {
        if (result is not var (doc, adopted)) return Task.CompletedTask;
        if (_closed)
        {
            doc.Dispose();
            return Task.CompletedTask;
        }
        _redactedDocuments[doc] = (HasAppliedRedactions, Cleaned & ~CleanupItems.InvisibleText);
        History.ReplaceDocument(description, doc, adopted);
        TrimMemoryStates();
        return Task.CompletedTask;
    }

    /// <summary>Puts text layers read before over pages (one undoable step), as „Text erkennen“ does.</summary>
    /// <param name="layers">The layers per page index.</param>
    /// <param name="description">The name of the step.</param>
    internal Task AddTextLayersAsync(Dictionary<int, PdfStream> layers, string description)
    {
        if (layers.Count == 0) return Task.CompletedTask;
        return WorkAsync(() =>
        {
            var items = History.Pages.ToArray();
            var main = Document;
            var password = _password;
            var options = SaveOptions(SaveVersion) with { Cleanup = Cleaned & ~CleanupItems.InvisibleText };
            var valid = layers.Where(l => (uint)l.Key < (uint)items.Length).ToDictionary(l => l.Key, l => l.Value);
            return Task.Run(() => valid.Count == 0 ? null : WithTextLayers(main, items, options, valid, password), CancellationToken.None);
        }, result => AdoptTextLayers(result, description));
    }

    /// <summary>
    /// The whole text recognition of the document — the invisible text of every page, line by line, under a heading per page — for „OCR
    /// anzeigen“ in „Verborgenes schwärzen“ (KONZEPT.md, section 5.6). Empty if there is none.
    /// </summary>
    public async Task<string> OcrTextAsync()
    {
        var all = new StringBuilder();
        for (var i = 0; i < PageCount; i++)
        {
            var page = InvisibleText(await GetTextAsync(i));
            if (page.Length == 0) continue;
            if (all.Length > 0) all.Append("\n\n");
            all.Append(Tr.T("— Seite {0} —", i + 1)).Append('\n').Append(page);
        }
        return all.ToString();
    }

    /// <summary>The invisible characters of a page with their spaces and line breaks; visible text is left out.</summary>
    internal static string InvisibleText(PageText text)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        var t = text.Text;
        for (var i = 0; i <= t.Length; i++)
        {
            if (i == t.Length || t[i] == '\n')
            {
                if (line.ToString().Trim() is { Length: > 0 } done) lines.Add(done);
                line.Clear();
                continue;
            }
            if (text.IsInvisible(i)) line.Append(t[i]);
            // a space between invisible characters belongs to them (inserted spaces count as visible)
            else if (char.IsWhiteSpace(t[i]) && line.Length > 0 && line[^1] != ' ' && i + 1 < t.Length && text.IsInvisible(i + 1)) line.Append(' ');
        }
        return string.Join('\n', lines);
    }

    /// <summary>The page being read and the lines being read, in page space (the view shows them in green, nothing goes into the file); null while nothing is read.</summary>
    public (int Page, IReadOnlyList<Core.Pdf.Graphics.RectD> Lines)? RecognitionPlace { get; private set; }

    /// <summary>
    /// Whether the view follows the recognition. The user scrolling ends it, so that the page stays where it is; it starts again with the button,
    /// or when the user comes back to the page being read after having left it (wish of 30.10.2026).
    /// </summary>
    [ObservableProperty]
    public partial bool RecognitionFollows { get; private set; } = true;

    /// <summary>Asks the view to bring the place being read into view (page, and the first line if known); the view does not count this as scrolling by the user.</summary>
    public event Action<int, Core.Pdf.Graphics.RectD?>? RecognitionFollowRequested;

    private bool _leftRecognitionPage;
    private long _lastPlaceReport;
    private int? _lastPlacePage;

    /// <summary>Reports the place on the UI thread (from the working thread only); on a page lines at most every 60 ms, the batches of small models are quicker than the eye.</summary>
    private void ReportPlace(int? page, IReadOnlyList<Core.Pdf.Graphics.RectD> lines)
    {
        if (lines.Count > 0)
        {
            var now = Environment.TickCount64;
            if (page == _lastPlacePage && now - _lastPlaceReport < 60) return;
            (_lastPlacePage, _lastPlaceReport) = (page, now);
        }
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) SetRecognitionPlace(page, lines);
        else Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_recognition is not null) SetRecognitionPlace(page, lines);
        });
    }

    private void SetRecognitionPlace(int? page, IReadOnlyList<Core.Pdf.Graphics.RectD> lines)
    {
        var previous = RecognitionPlace?.Page;
        RecognitionPlace = page is { } p ? (p, lines) : null;
        HiddenSpotsChanged?.Invoke();
        if (page is { } at && RecognitionFollows && (at != previous || lines.Count > 0))
            RecognitionFollowRequested?.Invoke(at, lines.Count > 0 ? lines[0] : null);
    }

    /// <summary>The user scrolled while the recognition runs.</summary>
    /// <param name="readingInView">Whether the page being read is in view now.</param>
    public void RecognitionScrolled(bool readingInView)
    {
        if (_recognition is null) return;
        if (RecognitionFollows)
        {
            RecognitionFollows = false;
            _leftRecognitionPage = !readingInView;
        }
        else if (!readingInView) _leftRecognitionPage = true;
        else if (_leftRecognitionPage)
        {
            _leftRecognitionPage = false;
            RecognitionFollows = true;
        }
    }

    /// <summary>The button „Autoscroll“: the view follows the recognition again, from where it is now.</summary>
    [RelayCommand]
    private void FollowRecognition()
    {
        RecognitionFollows = true;
        _leftRecognitionPage = false;
        if (RecognitionPlace is { } place) RecognitionFollowRequested?.Invoke(place.Page, place.Lines.Count > 0 ? place.Lines[0] : null);
    }

    /// <summary>Shows the progress on the UI thread (or at once without one, in tests).</summary>
    private void Report(string status)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) RecognitionStatus = status;
        else Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_recognition is not null) RecognitionStatus = status;
        });
    }
}
