using System.Collections.Concurrent;

namespace PdfKnead.Core.Scanning;

/// <summary>
/// One thread that runs every call into a scanner interface, one after the other. SANE does not allow calls from several threads at
/// once, and WIA objects belong to the apartment they were made in; a single worker covers both. Work is queued and awaited.
/// </summary>
internal sealed class ScanWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    /// <param name="name">The thread name (for diagnosis).</param>
    /// <param name="sta">Whether the thread is a single-threaded apartment (COM under Windows); ignored elsewhere.</param>
    public ScanWorker(string name, bool sta = false)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        if (sta && OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Whether the calling code already runs on the worker (then work is run directly, never queued behind itself).</summary>
    public bool IsCurrent => Thread.CurrentThread == _thread;

    /// <summary>Runs <paramref name="work"/> on the worker.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">The work.</param>
    /// <returns>Its result; exceptions are passed on.</returns>
    public Task<T> Run<T>(Func<T> work)
    {
        if (IsCurrent) return Task.FromResult(work());
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task;
    }

    /// <summary>Runs <paramref name="work"/> on the worker.</summary>
    /// <param name="work">The work.</param>
    /// <returns>Completes when it has run; exceptions are passed on.</returns>
    public Task Run(Action work) => Run(() => { work(); return true; });

    private void Loop()
    {
        foreach (var work in _queue.GetConsumingEnumerable()) work();
    }

    /// <summary>Ends the thread after the queued work.</summary>
    public void Dispose() => _queue.CompleteAdding();
}
