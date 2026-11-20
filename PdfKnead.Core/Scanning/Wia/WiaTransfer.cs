using System.Runtime.InteropServices.Marshalling;

namespace PdfKnead.Core.Scanning.Wia;

/// <summary>
/// The stream a WIA driver writes one page into (handed out in IWiaTransferCallback.GetNextStream). In pieces of 4 MB, never one array that
/// grows; at most <see cref="MaxBytes"/> — a driver writing more gets STG_E_MEDIUMFULL and the page is refused. The driver may seek back
/// (to finish the header) and set the size; gaps read as zeros. Calls come one after the other, but from threads of the COM runtime.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class PageStream : IStream, IPageData
{
    internal const int ChunkSize = 4 << 20;

    /// <summary>The most bytes of a page: the samples of the largest page plus room for header and padding.</summary>
    internal const long MaxBytes = WiaImage.MaxPixelBytes + (64 << 20);

    private const int StgEInvalidFunction = unchecked((int)0x80030001), StgEMediumFull = unchecked((int)0x80030070),
        StgEInvalidPointer = unchecked((int)0x80030009);

    private readonly List<byte[]> _chunks = [];
    private readonly Lock _gate = new();
    private long _length, _position;

    /// <summary>The most bytes this stream takes (<see cref="MaxBytes"/>; smaller in tests).</summary>
    internal long Limit { get; init; } = MaxBytes;

    /// <summary>Whether the driver tried to write more than <see cref="Limit"/>.</summary>
    public bool Overflowed { get; private set; }

    /// <inheritdoc/>
    public long Length
    {
        get { lock (_gate) return _length; }
    }

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> target)
    {
        lock (_gate) return Copy(offset, target);
    }

    // ---- ISequentialStream / IStream

    public int Read(byte* buffer, uint count, uint* read)
    {
        if (buffer == null && count > 0) return StgEInvalidPointer;
        lock (_gate)
        {
            var n = Copy(_position, new Span<byte>(buffer, (int)Math.Min(count, int.MaxValue)));
            _position += n;
            if (read != null) *read = (uint)n;
        }
        return Wia.SOk;
    }

    public int Write(byte* buffer, uint count, uint* written)
    {
        if (written != null) *written = 0;
        if (buffer == null && count > 0) return StgEInvalidPointer;
        lock (_gate)
        {
            if (_position + count > Limit)
            {
                Overflowed = true;
                return StgEMediumFull;
            }
            Grow(_position + count);
            var source = new ReadOnlySpan<byte>(buffer, (int)count);
            var at = _position;
            while (!source.IsEmpty)
            {
                var chunk = _chunks[(int)(at / ChunkSize)];
                var inChunk = (int)(at % ChunkSize);
                var n = Math.Min(source.Length, ChunkSize - inChunk);
                source[..n].CopyTo(chunk.AsSpan(inChunk));
                source = source[n..];
                at += n;
            }
            _position = at;
            _length = Math.Max(_length, at);
            if (written != null) *written = count;
        }
        return Wia.SOk;
    }

    public int Seek(long move, int origin, ulong* position)
    {
        lock (_gate)
        {
            var from = origin switch { 0 => 0, 1 => _position, 2 => _length, _ => -1 };
            if (from < 0) return StgEInvalidFunction;
            var target = from + move;
            if (target < 0 || target > Limit) return StgEInvalidFunction;
            _position = target;
            if (position != null) *position = (ulong)target;
        }
        return Wia.SOk;
    }

    public int SetSize(ulong size)
    {
        if (size > (ulong)Limit)
        {
            Overflowed = true;
            return StgEMediumFull;
        }
        lock (_gate)
        {
            var length = (long)size;
            if (length > _length) Grow(length);
            else
            {
                // shrinking: the bytes past the new end read as zeros again if the stream grows later
                for (var at = length; at < _length;)
                {
                    var chunk = _chunks[(int)(at / ChunkSize)];
                    var inChunk = (int)(at % ChunkSize);
                    var n = (int)Math.Min(ChunkSize - inChunk, _length - at);
                    chunk.AsSpan(inChunk, n).Clear();
                    at += n;
                }
                _chunks.RemoveRange((int)((length + ChunkSize - 1) / ChunkSize), _chunks.Count - (int)((length + ChunkSize - 1) / ChunkSize));
            }
            _length = length;
        }
        return Wia.SOk;
    }

    public int CopyTo(nint target, ulong count, ulong* read, ulong* written) => Wia.ENotImpl;

    public int Commit(int flags) => Wia.SOk;

    public int Revert() => Wia.SOk;

    public int LockRegion(ulong offset, ulong count, int type) => StgEInvalidFunction;

    public int UnlockRegion(ulong offset, ulong count, int type) => StgEInvalidFunction;

    public int Stat(StatStg* stat, int flags)
    {
        if (stat == null) return StgEInvalidPointer;
        *stat = default;
        stat->Type = 2;   // STGTY_STREAM; no name (a caller frees a null name without harm)
        lock (_gate) stat->Size = (ulong)_length;
        return Wia.SOk;
    }

    public int Clone(nint* stream) => Wia.ENotImpl;

    private void Grow(long length)
    {
        while ((long)_chunks.Count * ChunkSize < length) _chunks.Add(new byte[ChunkSize]);
    }

    private int Copy(long offset, Span<byte> target)
    {
        if (offset < 0 || offset >= _length) return 0;
        var total = (int)Math.Min(target.Length, _length - offset);
        var done = 0;
        while (done < total)
        {
            var at = offset + done;
            var chunk = _chunks[(int)(at / ChunkSize)];
            var inChunk = (int)(at % ChunkSize);
            var n = Math.Min(total - done, ChunkSize - inChunk);
            chunk.AsSpan(inChunk, n).CopyTo(target[done..]);
            done += n;
        }
        return total;
    }
}

/// <summary>
/// The callback of IWiaTransfer.Download: a new <see cref="PageStream"/> for every page, the page handed on as soon as the driver ends its
/// stream (pages read before an error stay), progress, and cancelling through the return value (S_FALSE stops the transfer; calling
/// IWiaTransfer.Cancel from another thread would break the rules of COM). No exception ever leaves a callback into the driver: the first
/// one is kept and thrown after Download (<see cref="Failure"/>).
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class PageCallback : IWiaTransferCallback, IStoppable
{
    private readonly Action<PageStream, int> _pageDone;
    private readonly IProgress<ScanProgress>? _progress;
    private readonly Lock _gate = new();
    private PageStream? _current;
    private int _page;
    private volatile bool _cancel;

    /// <param name="pageDone">Receives every complete page with its number (1-based); may throw (the transfer then stops).</param>
    /// <param name="progress">Receives the progress of the page being read.</param>
    public PageCallback(Action<PageStream, int> pageDone, IProgress<ScanProgress>? progress)
    {
        _pageDone = pageDone;
        _progress = progress;
    }

    /// <summary>The first exception inside a callback; the transfer was stopped by it.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>The number of pages handed on.</summary>
    public int Pages { get; private set; }

    /// <summary>The HRESULT of the last device status the driver reported (paper jam, cover open …), 0 if none.</summary>
    public int LastDeviceStatus { get; private set; }

    /// <summary>Whether the driver asked for a stream at all — if not, nothing was scanned (a new attempt is safe).</summary>
    public bool Started
    {
        get { lock (_gate) return _page > 0; }
    }

    /// <summary>Stops the transfer at the next callback.</summary>
    public void Cancel() => _cancel = true;

    /// <summary>After a successful Download: a last page whose stream the driver did not end explicitly is complete too.</summary>
    public void Complete()
    {
        try
        {
            Finish();
        }
        catch (Exception e)
        {
            Failure ??= e;
        }
    }

    public int TransferCallback(int flags, WiaTransferParams* parameters)
    {
        try
        {
            if (parameters != null)
                switch (parameters->Message)
                {
                    case Wia.TransferMsgStatus:
                        _progress?.Report(new ScanProgress(Math.Max(1, _page), Math.Clamp(parameters->PercentComplete, 0, 100) / 100.0));
                        break;
                    case Wia.TransferMsgEndOfStream:
                    case Wia.TransferMsgEndOfTransfer:
                        Finish();
                        break;
                    case Wia.TransferMsgDeviceStatus when parameters->ErrorStatus < 0:
                        LastDeviceStatus = parameters->ErrorStatus;
                        break;
                }
        }
        catch (Exception e)
        {
            Failure ??= e;
        }
        return _cancel || Failure is not null ? Wia.SFalse : Wia.SOk;
    }

    public int GetNextStream(int flags, nint itemName, nint fullItemName, out IStream? destination)
    {
        destination = null;
        try
        {
            // a driver asking for the next stream without ending the last one: that page is complete
            Finish();
            // S_FALSE tells the driver to stop (as its own GetNextStream answers a cancelled transfer)
            if (_cancel || Failure is not null) return Wia.SFalse;
            var stream = new PageStream();
            lock (_gate)
            {
                _current = stream;
                _page++;
            }
            destination = stream;
            return Wia.SOk;
        }
        catch (Exception e)
        {
            Failure ??= e;
            return Wia.EFail;
        }
    }

    private void Finish()
    {
        PageStream? page;
        int number;
        lock (_gate)
        {
            (page, number) = (_current, _page);
            _current = null;
        }
        if (page is null) return;
        if (page.Overflowed) throw new ScanException(Tr.T("Die Seite wäre zu groß — bitte eine kleinere Auflösung oder Fläche wählen."));
        if (page.Length == 0) return;   // an empty stream is no page (drivers send one at the end of the feeder)
        _pageDone(page, number);
        Pages++;
    }
}

/// <summary>
/// The callback of a WIA 1.0 band transfer (IWiaDataTransfer.idtGetBandedData): each band of a page written into a <see cref="PageStream"/>
/// at its offset (the image starts with its info header, without the file header), the page handed on at the end of the page or
/// of the transfer, progress, and cancelling through the return value. One instance for all pages of a scan (the feeder is read with one
/// transfer per page); no exception ever leaves a callback into the driver.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class BandCallback : IWiaDataCallback, IStoppable
{
    private readonly Action<PageStream, int> _pageDone;
    private readonly IProgress<ScanProgress>? _progress;
    private readonly Lock _gate = new();
    private PageStream? _current;
    private int _page;
    private volatile bool _cancel;

    /// <param name="pageDone">Receives every complete page with its number (1-based); may throw (the transfer then stops).</param>
    /// <param name="progress">Receives the progress of the page being read.</param>
    public BandCallback(Action<PageStream, int> pageDone, IProgress<ScanProgress>? progress)
    {
        _pageDone = pageDone;
        _progress = progress;
    }

    /// <summary>The first exception inside a callback; the transfer was stopped by it.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>The number of pages handed on.</summary>
    public int Pages { get; private set; }

    /// <summary>Stops the transfer at the next callback.</summary>
    public void Cancel() => _cancel = true;

    /// <summary>After a transfer that returned success: a page whose end the driver did not report is complete too.</summary>
    public void Complete()
    {
        try
        {
            Finish();
        }
        catch (Exception e)
        {
            Failure ??= e;
        }
    }

    private long _bytes;

    /// <summary>The bytes of image data received so far (a transfer that added none scanned nothing: a new attempt is safe).</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Drops a page begun but not finished (the transfer failed).</summary>
    public void Drop()
    {
        lock (_gate) _current = null;
    }

    public int BandedDataCallback(int message, int status, int percent, int offset, int length, int reserved, int resLength, byte* buffer)
    {
        try
        {
            switch (message)
            {
                case Wia.ItMsgData when length > 0 && buffer != null:
                    PageStream page;
                    lock (_gate)
                    {
                        if (_current is null)
                        {
                            _current = new PageStream();
                            _page++;
                        }
                        page = _current;
                    }
                    // each band at its place: lOffset counts from the start of the page's data. The compatibility layer passes on where the
                    // driver writes in its stream — Windows' eSCL driver writes the rows of a bottom-up BMP from the end towards the start as it
                    // scans from the top; appended in the order they come, the page came mirrored top to bottom (Fehlergedächtnis 855)
                    if (offset >= 0 && page.Seek(offset, 0, null) != Wia.SOk)
                        throw new ScanException(Tr.T("Der Scanner hat ein unvollständiges oder beschädigtes Bild geliefert."));
                    if (page.Write(buffer, (uint)length, null) != Wia.SOk)
                        throw new ScanException(Tr.T("Die Seite wäre zu groß — bitte eine kleinere Auflösung oder Fläche wählen."));
                    Interlocked.Add(ref _bytes, length);
                    _progress?.Report(new ScanProgress(Math.Max(1, _page), Math.Clamp(percent, 0, 100) / 100.0));
                    break;
                case Wia.ItMsgStatus:
                    _progress?.Report(new ScanProgress(Math.Max(1, _page + (_current is null ? 1 : 0)), Math.Clamp(percent, 0, 100) / 100.0));
                    break;
                case Wia.ItMsgNewPage:
                case Wia.ItMsgTermination:
                    Finish();
                    break;
            }
        }
        catch (Exception e)
        {
            Failure ??= e;
        }
        return _cancel || Failure is not null ? Wia.SFalse : Wia.SOk;
    }

    private void Finish()
    {
        PageStream? page;
        int number;
        lock (_gate)
        {
            (page, number) = (_current, _page);
            _current = null;
        }
        if (page is null || page.Length == 0) return;
        _pageDone(page, number);
        Pages++;
    }
}

/// <summary>A transfer callback that can be told to stop at its next call (both kinds, so one cancel reaches whichever runs).</summary>
internal interface IStoppable
{
    /// <summary>Stops the transfer at the next callback.</summary>
    void Cancel();
}