namespace PdfKnead.ViewModels;

/// <summary>What happened to a document's file on disk since it was opened or last saved.</summary>
public enum DiskChange
{
    /// <summary>Nothing (or nothing to say: untitled, being saved, already answered).</summary>
    None,

    /// <summary>Changed by another program; the opened version is still completely readable (in memory, or the file was replaced under its name).</summary>
    Changed,

    /// <summary>Written anew in place by another program: the opened version of a large file can no longer be read completely.</summary>
    OverwrittenInPlace,

    /// <summary>The file no longer exists under its name (deleted or moved).</summary>
    Deleted,
}

/// <summary>The document's file on disk (KONZEPT.md, „Datei von außen geändert“; docs/FEHLERGEDAECHTNIS.md, No. 240).</summary>
public sealed partial class DocumentViewModel
{
    // Length and time of last writing of the file when opened or last saved (or when a change was answered); null: none (untitled)
    private (long Length, DateTime Written)? _diskStamp;
    private bool _deletionAnswered;

    private static (long Length, DateTime Written)? StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Remembers the file as it is now (after opening, saving, or once the user has answered about a change).</summary>
    internal void RememberDiskState()
    {
        _diskStamp = IsUntitled ? null : StampOf(Path);
        _deletionAnswered = false;
    }

    /// <summary>What happened to the file since then; <see cref="DiskChange.None"/> while it is being saved, for untitled documents and after an answer.</summary>
    /// <returns>The change.</returns>
    public DiskChange CheckDisk()
    {
        if (IsUntitled || IsWorking || _closed || _diskStamp is not { } known) return DiskChange.None;
        if (StampOf(Path) is not { } now) return _deletionAnswered ? DiskChange.None : DiskChange.Deleted;
        if (now == known) return DiskChange.None;
        return Document.IsUnchangedOnDisk() == false ? DiskChange.OverwrittenInPlace : DiskChange.Changed;
    }

    /// <summary>The user has answered about the change: no further question until the file changes again.</summary>
    /// <param name="change">The change answered.</param>
    internal void AnswerDiskChange(DiskChange change)
    {
        if (change == DiskChange.Deleted) _deletionAnswered = true;
        else _diskStamp = StampOf(Path);
    }
}
