using Avalonia.Threading;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// Watching the folders of the open files (docs/FEHLERGEDAECHTNIS.md, No. 240): a change there, or the window becoming active again,
/// lets the view model check its files and ask about those another program changed. The watchers live with the window.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private DispatcherTimer? _diskCheck;

    private void StartDiskWatch()
    {
        if (_vm is null) return;
        _vm.AskChoice = async (heading, message, first, second) => await new ChoiceWindow(heading, message, first, second).ShowDialog<int>(this);
        _vm.AskChoices = async request => await new ChoiceWindow(request).ShowDialog<ChoiceAnswer>(this);
        _vm.Documents.CollectionChanged += (_, _) => SyncWatchers();
        Activated += (_, _) => ScheduleDiskCheck();
        SyncWatchers();
    }

    /// <summary>One watcher per folder of an open file; folders without one are no longer watched.</summary>
    private void SyncWatchers()
    {
        if (_vm is null) return;
        var folders = _vm.WatchedFolders;
        foreach (var gone in _watchers.Keys.Where(f => !folders.Contains(f)).ToList())
        {
            _watchers[gone].Dispose();
            _watchers.Remove(gone);
        }
        foreach (var folder in folders)
        {
            if (_watchers.ContainsKey(folder) || !Directory.Exists(folder)) continue;
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                watcher.Changed += OnDiskEvent;
                watcher.Created += OnDiskEvent;
                watcher.Deleted += OnDiskEvent;
                watcher.Renamed += OnDiskEvent;
                watcher.EnableRaisingEvents = true;
                _watchers[folder] = watcher;
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // without a watcher the check still runs whenever the window becomes active
            }
        }
    }

    private void OnDiskEvent(object? sender, FileSystemEventArgs e) => Dispatcher.UIThread.Post(ScheduleDiskCheck);

    /// <summary>Checks shortly after the last event: a program writing a file raises many of them.</summary>
    private void ScheduleDiskCheck()
    {
        if (_vm is null) return;
        _diskCheck ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, async (_, _) =>
        {
            _diskCheck!.Stop();
            if (_vm is not null) await _vm.CheckDiskAsync();
        });
        _diskCheck.Stop();
        _diskCheck.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _diskCheck?.Stop();
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
    }
}
