using Avalonia.Threading;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.ViewModels;

/// <summary>
/// Text editing: the text blocks of the pages, detected in the background (<see cref="TextBlocks"/>) and cached per page content.
/// Only pages viewed in this mode are processed, at most <see cref="MaxBlockPages"/> at a time (more if more pages are visible); the
/// most recently viewed ones are kept. See KONZEPT.md, section 5.14.
/// </summary>
public sealed partial class DocumentViewModel
{
    private const int MaxBlockPages = 24;

    private readonly Lock _blockGate = new();
    private readonly Dictionary<PdfDictionary, (IReadOnlyList<PageTextBlock> Blocks, LinkedListNode<PdfDictionary> Node)> _blocks =
        new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<PdfDictionary> _blockOrder = new();
    private readonly Dictionary<PdfDictionary, CancellationTokenSource> _blocksRunning = new(ReferenceEqualityComparer.Instance);
    private CancellationTokenSource _blockCancel = new();
    private int _blockLimit = MaxBlockPages;
    private bool _converting;
    private bool _signedEditHintShown;

    /// <summary>Tool changed: the parts of „Bearbeiten“ and „Verborgenes schwärzen“ follow (<see cref="EditToolChanged"/>).</summary>
    partial void OnToolChanged(ViewTool oldValue, ViewTool newValue)
    {
        HiddenToolChanged(oldValue, newValue);
        EditToolChanged(oldValue, newValue);
        MeasureToolChanged(oldValue, newValue);
    }

    /// <summary>Raised when the blocks of a page have been detected (the view draws their frames).</summary>
    public event Action? BlocksReady;

    /// <summary>Returns the blocks of a page and starts their detection if necessary.</summary>
    /// <param name="page">Page index.</param>
    /// <returns>The blocks; null while they are still being detected (<see cref="BlocksReady"/> follows); empty for an index outside the document.</returns>
    public IReadOnlyList<PageTextBlock>? BlocksOn(int page)
    {
        if ((uint)page >= (uint)Pages.Count) return [];
        var item = Pages[page].Item;
        var key = item.Page.Dictionary;
        CancellationTokenSource running;
        lock (_blockGate)
        {
            if (_blocks.TryGetValue(key, out var done))
            {
                // Viewed: move to the front; if only the time of detection counted, visible pages could be evicted
                _blockOrder.Remove(done.Node);
                _blockOrder.AddFirst(done.Node);
                return done.Blocks;
            }
            if (_blocksRunning.ContainsKey(key)) return null;
            running = CancellationTokenSource.CreateLinkedTokenSource(_blockCancel.Token);
            _blocksRunning[key] = running;
        }
        var cancel = running.Token;
        _ = Task.Run(() =>
        {
            IReadOnlyList<PageTextBlock> blocks = [];
            try
            {
                using var use = Renderer.Use(item.Source);
                if (use is not null) blocks = TextBlocks.Find(item.Source, item.Page, cancel);
            }
            catch (OperationCanceledException)
            {
                lock (_blockGate) Finished(key, running);
                return;
            }
            catch (Exception e)
            {
                Core.Platform.AppLog.Write("Textblöcke nicht erkannt", e.ToString());   // damaged page: no blocks, everything else carries on
            }
            lock (_blockGate)
            {
                Finished(key, running);
                if (cancel.IsCancellationRequested) return;
                _blocks[key] = (blocks, _blockOrder.AddFirst(key));
                while (_blockOrder.Count > _blockLimit && _blockOrder.Last is { } oldest)
                {
                    _blocks.Remove(oldest.Value);
                    _blockOrder.RemoveLast();
                }
            }
            Dispatcher.UIThread.Post(() => BlocksReady?.Invoke());
        }, CancellationToken.None);
        return null;
    }

    /// <summary>Detection for <paramref name="key"/> finished or cancelled (only unregisters its own entry, since a new one may already be running).</summary>
    private void Finished(PdfDictionary key, CancellationTokenSource running)
    {
        if (_blocksRunning.TryGetValue(key, out var current) && ReferenceEquals(current, running)) _blocksRunning.Remove(key);
        running.Dispose();
    }

    /// <summary>
    /// The view shows the pages <paramref name="pages"/> (called before requesting their blocks): their blocks stay cached even if
    /// there are more than <see cref="MaxBlockPages"/>, and detections for pages no longer visible are cancelled (otherwise fast
    /// scrolling through a long document would finish computing every page skimmed past).
    /// </summary>
    /// <param name="pages">Indices of the visible pages.</param>
    public void ShowingBlocks(IReadOnlyCollection<int> pages)
    {
        var shown = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var page in pages)
            if ((uint)page < (uint)Pages.Count) shown.Add(Pages[page].Item.Page.Dictionary);
        lock (_blockGate)
        {
            _blockLimit = Math.Max(MaxBlockPages, shown.Count + 4);
            foreach (var (key, running) in _blocksRunning.ToList())
                if (!shown.Contains(key))
                {
                    running.Cancel();
                    _blocksRunning.Remove(key);
                }
        }
    }

    // ------------------------------------------------------------ Editing formatted text

    /// <summary>Editor of the formatted text currently being edited (null = none).</summary>
    public RichTextEditor? RichEditor { get; private set; }

    /// <summary>Starts editing formatted text with the caret at <paramref name="caret"/>; ends any edit still open.</summary>
    /// <param name="element">The rich text element.</param>
    /// <param name="caret">Caret position (character index).</param>
    /// <returns>The new editor.</returns>
    public RichTextEditor BeginRichEdit(ElementRef element, int caret)
    {
        EndRichEdit();
        var editor = new RichTextEditor(this, element, caret);
        editor.Updated += OnRichEditorUpdated;
        RichEditor = editor;
        SelectedElement = element;
        OnPropertyChanged(nameof(RichEditor));
        if (Tool == ViewTool.Edit) EditTarget = EditTarget.Text;
        return editor;
    }

    /// <summary>
    /// Ends editing; a block that has become empty disappears (as with text elements). A page block that was not changed is restored
    /// as it was, so the page stays untouched. See KONZEPT.md, section 5.14.
    /// </summary>
    public void EndRichEdit()
    {
        if (RichEditor is not { } editor) return;
        editor.Updated -= OnRichEditorUpdated;
        RichEditor = null;
        if (EditTarget == EditTarget.Text) EditTarget = EditTarget.None;
        var present = ElementsOn(editor.Element.Page).Contains(editor.Current);
        // Unchanged: retract exactly the conversion step (not undo, which might hit the step of another block and would lose the
        // redo history)
        if (editor.Converted is { } converted && !editor.Changed && present && History.Retract(converted))
        {
            SelectedElement = null;
        }
        else if (editor.Changed && editor.Current.Content.Text.Trim().Length == 0 && present)
        {
            // Empty: the block goes entirely (for a page block its text stays removed, since that was the change)
            History.RemoveElements(editor.Element.Page, [editor.Current], RichTextEditor.Description);
        }
        OnPropertyChanged(nameof(RichEditor));
    }

    /// <summary>
    /// Edits a block of the page: the page without its glyphs (computed in the background, as for redaction) and, in their place, the
    /// block as formatted text; one step that subsequent typing continues. See KONZEPT.md, section 5.14.
    /// </summary>
    /// <param name="page">Page index.</param>
    /// <param name="block">The detected block.</param>
    /// <returns>The editor, or null if it was not possible (a notice explains why where applicable).</returns>
    public async Task<RichTextEditor?> EditBlockAsync(int page, PageTextBlock block)
    {
        // One conversion at a time: a second click while the first is running (seconds for a large plan) is ignored
        if ((uint)page >= (uint)History.Count || block.Locked is not null || _converting) return null;
        EndRichEdit();
        var item = History.Pages[page];
        RichTextElement element;
        Redactor.HiddenContentResult? removed;
        _converting = true;
        try
        {
            (element, removed) = await Task.Run(() =>
            {
                using var use = Renderer.Use(item.Source);
                if (use is null) return (null!, null);
                var e = EditedText.FromBlock(item.Source, block);
                return (e, Redactor.RemoveGlyphs(item.Source, item.Page, block.Glyphs.ToHashSet(), block.Paths.ToHashSet()));
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Core.Platform.AppLog.Write("Text bearbeiten", e.ToString());
            Notify(Tr.T("Dieser Text lässt sich nicht bearbeiten: {0}", e.Message));
            return null;
        }
        finally
        {
            _converting = false;
        }
        // Changed in the meantime (undo, another page, mode left …): do not apply to a different state
        if (Tool != ViewTool.Edit) return null;
        if (removed is null || element is null || (uint)page >= (uint)History.Count || !ReferenceEquals(History.Pages[page], item))
        {
            if (removed is null && element is not null) Notify(Tr.T("Dieser Text lässt sich nicht bearbeiten (seine Zeichen wurden im Inhalt nicht gefunden)."));
            return null;
        }
        // the marked content whose characters the block takes: in a tagged file its text goes into their element (6.6 i)
        if (removed.AlternateTextMcids.Count > 0) element = element with { ReplacesMcids = [.. removed.AlternateTextMcids.Order()] };
        var pages = History.Pages.ToArray();
        pages[page] = item.WithEditedPage(removed, [.. item.Elements, element]);
        History.Apply(RichTextEditor.Description, pages, element);
        var editor = BeginRichEdit(new ElementRef(page, element), 0);
        editor.Converted = History.Pages;
        return editor;
    }

    /// <summary>Raised when the caret or the text of the editor has changed.</summary>
    public event Action? RichEditorUpdated;

    private void OnRichEditorUpdated() => RichEditorUpdated?.Invoke();

    /// <summary>After undo and similar steps: closes the editor if its element no longer exists.</summary>
    private void ValidateRichEditor()
    {
        if (RichEditor is { Applying: false } editor && !ElementsOn(editor.Element.Page).Contains(editor.Element.Element)) EndRichEdit();
    }

    /// <summary>Discards the detected blocks and cancels running detections (mode off, after saving, document closed).</summary>
    private void ResetBlocks()
    {
        lock (_blockGate)
        {
            // the old source is cancelled, not disposed: running work still links to its token, and without a timer it holds nothing
            _blockCancel.Cancel();
            _blockCancel = new CancellationTokenSource();
            _blocks.Clear();
            _blockOrder.Clear();
            _blocksRunning.Clear();   // the linked cancellations are cancelled together with _blockCancel
            _blockLimit = MaxBlockPages;
        }
    }
}
