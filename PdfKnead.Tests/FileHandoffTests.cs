using System.Text;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests;

/// <summary>
/// „Dateien öffnen: im zuletzt benutzten Fenster“ (decided on 16.11.2026): a new start hands its files to an open window over a local
/// pipe of the user's own account. The message is refused as a whole unless it holds the expected words and full paths within the limits —
/// a stranger on the pipe gets nothing opened, and a length of its choosing allocates nothing.
/// </summary>
public sealed class FileHandoffTests
{
    private static string Name() => "pdf-knead-test-" + Guid.NewGuid().ToString("N");

    private static string[] TwoFiles()
    {
        var folder = Directory.CreateTempSubdirectory("pdf-knead-handoff-").FullName;
        string[] files = [Path.Combine(folder, "eins.pdf"), Path.Combine(folder, "zwei mit Leerzeichen und Ümlaut.pdf")];
        foreach (var f in files) File.WriteAllText(f, "%PDF-1.7");
        return files;
    }

    [Fact]
    public async Task Handover_ReachesTheOpenWindow()
    {
        var name = Name();
        var got = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var listening = FileHandoff.Listen(name, paths => got.TrySetResult(paths)))
        {
            await listening.Ready.WaitAsync(TimeSpan.FromSeconds(30));
            var files = TwoFiles();
            Assert.True(await Task.Run(() => FileHandoff.TrySend(name, files, TimeSpan.FromSeconds(10))));
            Assert.Equal(files, await got.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            // the window listens on: a second start reaches it too
            var again = new TaskCompletionSource<IReadOnlyList<string>>();
            got = again;
            Assert.True(await Task.Run(() => FileHandoff.TrySend(name, [files[0]], TimeSpan.FromSeconds(10))));
        }
    }

    /// <summary>Nobody listening: the answer "no" comes soon — the pipe is waited for a second at most (a time test, FG 399).</summary>
    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void NobodyListening_FalseSoon()
    {
        var files = TwoFiles();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(FileHandoff.TrySend(Name(), files, TimeSpan.FromSeconds(10)));
        Assert.True(watch.ElapsedMilliseconds < 2000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Several starts at once (the file manager opening several files): each reaches the window, also under the load of the full run — the
    /// window keeps four pipes listening, so that the system connects them without the program.
    /// </summary>
    [Fact]
    public async Task SeveralStartsAtOnce_AllReachTheWindow()
    {
        var name = Name();
        var got = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listening = FileHandoff.Listen(name, paths =>
        {
            foreach (var path in paths) got.Add(path);
        });
        await listening.Ready.WaitAsync(TimeSpan.FromSeconds(30));
        var files = TwoFiles();
        var starts = Enumerable.Range(0, FileHandoff.Listener.Waiting)
            .Select(i => Task.Run(() => FileHandoff.TrySend(name, [files[i % 2]], TimeSpan.FromSeconds(10)))).ToArray();
        Assert.All(await Task.WhenAll(starts), Assert.True);
        Assert.Equal(FileHandoff.Listener.Waiting, got.Count);
    }

    /// <summary>A relative path is refused by the window (answer 0) and nothing is opened.</summary>
    [Fact]
    public async Task RelativePath_Refused_NothingOpened()
    {
        var name = Name();
        var opened = false;
        using (var listening = FileHandoff.Listen(name, _ => opened = true))
        {
            await listening.Ready.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(await Task.Run(() => FileHandoff.TrySend(name, ["relativ.pdf"], TimeSpan.FromSeconds(10))));
            // the window still listens after the refusal
            Assert.True(await Task.Run(() => FileHandoff.TrySend(name, [TwoFiles()[0]], TimeSpan.FromSeconds(10))));
        }
        Assert.True(opened);   // only the second message
    }

    private static byte[] Text(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return [.. BitConverter.GetBytes(bytes.Length), .. bytes];
    }

    private static byte[] Message(string magic, int count, params byte[][] paths) =>
        [.. Text(magic), .. BitConverter.GetBytes(count), .. paths.SelectMany(p => p)];

    private static Task<IReadOnlyList<string>?> Read(byte[] message) => FileHandoff.ReadAsync(new MemoryStream(message), CancellationToken.None);

    [Fact]
    public async Task Message_RefusedUnlessWithinTheLimits()
    {
        var full = Path.Combine(Path.GetTempPath(), "a.pdf");
        Assert.Equal([full], await Read(Message("PDF-Knead Dateien 1", 1, Text(full))));
        Assert.Null(await Read(Message("Fremd", 1, Text(full))));                              // other words
        Assert.Null(await Read(Message("PDF-Knead Dateien 1", 0)));                             // no file
        Assert.Null(await Read(Message("PDF-Knead Dateien 1", FileHandoff.MaxFiles + 1, Text(full))));
        Assert.Null(await Read(Message("PDF-Knead Dateien 1", 1, Text("relativ.pdf"))));
        Assert.Null(await Read(Message("PDF-Knead Dateien 1", 1, Text(full + "\0x"))));
        // a length of the sender's choosing: refused before anything is read or allocated
        Assert.Null(await Read(Message("PDF-Knead Dateien 1", 1, BitConverter.GetBytes(int.MaxValue))));
        Assert.Null(await Read([.. BitConverter.GetBytes(-5)]));
        // bytes that are no UTF-8
        Assert.Null(await Read(Message("PDF-Knead Dateien 1", 1, [.. BitConverter.GetBytes(3), 0xFF, 0xFE, 0xFD])));
        // a message cut short ends the reading (the window listens on)
        await Assert.ThrowsAsync<EndOfStreamException>(() => Read([.. Text("PDF-Knead Dateien 1"), 1, 0]));
    }

    /// <summary>Only existing files are handed over; anything else starts a window as before (nobody is asked).</summary>
    [Fact]
    public void HandOver_OnlyExistingFiles()
    {
        Assert.False(FileHandoff.TryHandOver([], TimeSpan.FromSeconds(1)));
        Assert.False(FileHandoff.TryHandOver([Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf")], TimeSpan.FromSeconds(1)));
        Assert.False(FileHandoff.TryHandOver([.. Enumerable.Repeat(TwoFiles()[0], FileHandoff.MaxFiles + 1)], TimeSpan.FromSeconds(1)));
    }

    /// <summary>Under Linux the socket lies in the user's runtime folder when there is one; otherwise the name alone (.NET's own place).</summary>
    [Fact]
    public void PipeName_InTheRuntimeFolder()
    {
        var folder = Directory.CreateTempSubdirectory("pdf-knead-laufzeit-").FullName;
        Assert.Equal(Path.Combine(folder, "pdf-knead-oeffnen-42"), FileHandoff.PipeName(42, folder));
        Assert.Equal("pdf-knead-oeffnen-42", FileHandoff.PipeName(42, null));
        Assert.Equal("pdf-knead-oeffnen-42", FileHandoff.PipeName(42, "relativ"));
        Assert.Equal("pdf-knead-oeffnen-42", FileHandoff.PipeName(42, Path.Combine(folder, "fehlt")));
        if (OperatingSystem.IsWindows()) Assert.Equal("pdf-knead-oeffnen-42", FileHandoff.PipeName(42));
    }

    [Fact]
    public void Setting_SavedAndLoaded_OffWhenMissing()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("pdf-knead-settings-").FullName, "settings.json");
        new AppSettings(OpenInRunningWindow: true).Save(file);
        Assert.True(AppSettings.Load(file).OpenInRunningWindow);
        File.WriteAllText(file, "{\"Language\":\"de\"}");   // settings of an earlier version
        Assert.False(AppSettings.Load(file).OpenInRunningWindow);
    }
}
