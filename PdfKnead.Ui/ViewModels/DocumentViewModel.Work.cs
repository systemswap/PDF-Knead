namespace PdfKnead.ViewModels;

/// <summary>
/// Background work that reads from the document's files: saving, redacting, exporting, extracting, splitting, checking. While it
/// runs,
/// <list type="bullet">
/// <item>it is registered with the renderer as a reader, so closing and releasing wait for it (otherwise the file would be released
/// under the reader: access violation, the program crashes);</item>
/// <item>the history is locked, so editing and undo change nothing (otherwise changes made during „Speichere …“ would be lost, and
/// marks set during redaction would be missing afterwards);</item>
/// <item><see cref="IsWorking"/> reports it, so that the commands are greyed out immediately;</item>
/// <item>a second piece of work waits until the first has finished.</item>
/// </list>
/// </summary>
public sealed partial class DocumentViewModel
{
    private int _work;
    private TaskCompletionSource? _idle;
    private bool _closed;

    /// <summary>Whether background work is running (editing and further work are locked).</summary>
    public bool IsWorking => _work > 0;

    /// <summary>Returns a task that completes when no work is running any more.</summary>
    public Task WhenIdle() => _idle?.Task ?? Task.CompletedTask;

    /// <summary>
    /// Runs a piece of work (see above); after the document has been closed it throws <see cref="OperationCanceledException"/>.
    /// <paramref name="then"/> takes over the result after the readers have been unregistered (taking over releases old files and
    /// waits for all readers to do so), but while the history is still locked.
    /// </summary>
    /// <param name="work">The work; reads the document's files.</param>
    /// <param name="then">Optional step that takes over the result; null = none.</param>
    /// <returns>The result of <paramref name="work"/>.</returns>
    public async Task<T> WorkAsync<T>(Func<Task<T>> work, Func<T, Task>? then = null)
    {
        while (_work > 0) await WhenIdle();
        if (_closed) throw new OperationCanceledException();
        var uses = AllSources().Select(Renderer.Use).OfType<IDisposable>().ToList();
        _work = 1;
        _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        History.Locked = true;
        OnPropertyChanged(nameof(IsWorking));
        try
        {
            T result;
            try
            {
                result = await work();
            }
            finally
            {
                foreach (var use in uses) use.Dispose();
            }
            if (then is not null) await then(result);
            return result;
        }
        finally
        {
            _work = 0;
            History.Locked = false;
            var idle = _idle;
            _idle = null;
            idle?.TrySetResult();
            OnPropertyChanged(nameof(IsWorking));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }
    }

    /// <summary>Like <see cref="WorkAsync{T}"/>, without a result.</summary>
    /// <param name="work">The work; reads the document's files.</param>
    public Task WorkAsync(Func<Task> work) => WorkAsync(async () =>
    {
        await work();
        return true;
    });
}
