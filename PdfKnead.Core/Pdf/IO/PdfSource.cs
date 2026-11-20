using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;
using PdfKnead.Core.Localization;

namespace PdfKnead.Core.Pdf.IO;

/// <summary>
/// Bytes of a PDF file with random access. Large files are not read completely, so a 500 MB drawing occupies hardly any memory
/// when opened: under Windows they are memory-mapped, elsewhere they are read in blocks of 64 KiB as needed.
/// </summary>
/// <remarks>
/// Reading from several threads at the same time is allowed (the object has no position; each reader keeps its own). After
/// <see cref="Dispose"/>, spans obtained from a mapped source are invalid.
/// <para>
/// Why not map everywhere: another program may shorten the file in place while it is open. Under Windows a mapped file cannot be
/// shortened; under Linux the next access to the lost part ends the process (SIGBUS, which .NET cannot catch) — all unsaved work
/// would be lost. Read in blocks, a shortened file only makes a read fail with a <see cref="PdfException"/>
/// (docs/FEHLERGEDAECHTNIS.md, No. 240).
/// </para>
/// </remarks>
public sealed unsafe class PdfSource : IDisposable
{
    private readonly byte[]? _array;
    private readonly MemoryMappedFile? _map;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly byte* _pointer;
    private readonly PdfSource? _parent;
    private bool _disposed;

    // The open file (kept open, so that a file deleted meanwhile stays readable), with the time it was last written when opened
    private readonly SafeFileHandle? _file;
    private readonly FileStream? _stream;   // the mapped file (Windows)
    private readonly DateTime _written;

    // Reading in blocks: a direct-mapped cache of 256 blocks of 64 KiB (16 MiB at most) per file; each block an immutable array, so
    // readers on several threads need no lock (a block replaced meanwhile stays valid for whoever still holds it)
    private const int BlockShift = 16, BlockSize = 1 << BlockShift, Slots = 256;
    private readonly Block?[]? _blocks;

    private sealed class Block(long number, byte[] data)
    {
        public readonly long Number = number;
        public readonly byte[] Data = data;
    }

    private PdfSource(byte[] array, int length)
    {
        _array = array;
        Length = Math.Clamp(length, 0, array.Length);
    }

    private PdfSource(MemoryMappedFile map, MemoryMappedViewAccessor view, long length, FileStream? stream = null)
    {
        _map = map;
        _view = view;
        _stream = stream;
        _file = stream?.SafeFileHandle;
        if (_file is not null) _written = File.GetLastWriteTimeUtc(_file);
        Length = length;
        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _pointer = p + view.PointerOffset;
    }

    private PdfSource(SafeFileHandle file, long length)
    {
        _file = file;
        _written = File.GetLastWriteTimeUtc(file);
        _blocks = new Block?[Slots];
        Length = length;
    }

    private PdfSource(PdfSource parent, long length)
    {
        _parent = parent;
        _array = parent._array;
        _pointer = parent._pointer;
        _blocks = parent._blocks;
        _file = parent._blocks is null ? null : parent._file;
        Length = Math.Clamp(length, 0, parent.Length);
    }

    /// <summary>Length of the data in bytes.</summary>
    public long Length { get; }

    /// <summary>
    /// Opens a file; files larger than 4 MiB stay open and are memory-mapped (Windows) or read in blocks (elsewhere), smaller ones are
    /// read completely.
    /// </summary>
    /// <param name="path">Path of the file.</param>
    /// <returns>The source; the caller disposes it.</returns>
    /// <remarks>A large file stays open; other programs may continue to read, write and delete it.</remarks>
    public static PdfSource FromFile(string path)
    {
        var length = new FileInfo(path).Length;
        // Empty files cannot be memory-mapped
        if (length == 0) return FromBytes([]);
        // Small files are read completely: no mapping needed, and the file is not kept open
        if (length <= 4 * 1024 * 1024) return FromBytes(File.ReadAllBytes(path));

        if (!OperatingSystem.IsWindows())
            return new PdfSource(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), length);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var map = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
            var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return new PdfSource(map, view, length, stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether the open file still has the length and time of last writing it had when opened — false if another program wrote into it
    /// in place (then the data read from it may no longer fit together). A file replaced under its name (written anew and renamed, as most
    /// programs save) or deleted leaves the open one unchanged. Null for sources without an open file (read completely, in memory).
    /// </summary>
    /// <returns>True if unchanged, false if changed in place, null without a file.</returns>
    public bool? IsUnchangedOnDisk()
    {
        var file = _parent?._file ?? _file;
        if (file is null || file.IsClosed) return null;
        try
        {
            return RandomAccess.GetLength(file) == (_parent ?? this).Length && File.GetLastWriteTimeUtc(file) == (_parent ?? this)._written;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies the bytes into an anonymous memory area without a file (memory only, or the system's swap space if
    /// needed), for intermediate results that must never exist as a file on disk; sizes above 2 GB are possible.
    /// </summary>
    internal static PdfSource FromChunks(IReadOnlyList<byte[]> chunks, long length)
    {
        var map = MemoryMappedFile.CreateNew(null, Math.Max(1, length), MemoryMappedFileAccess.ReadWrite);
        try
        {
            using (var writer = map.CreateViewStream(0, Math.Max(1, length), MemoryMappedFileAccess.Write))
            {
                var left = length;
                foreach (var chunk in chunks)
                {
                    if (left <= 0) break;
                    var count = (int)Math.Min(chunk.Length, left);
                    writer.Write(chunk, 0, count);
                    left -= count;
                }
            }
            var view = map.CreateViewAccessor(0, Math.Max(1, length), MemoryMappedFileAccess.Read);
            return new PdfSource(map, view, length);
        }
        catch
        {
            map.Dispose();
            throw;
        }
    }

    /// <summary>Wraps an array without copying it.</summary>
    /// <param name="data">The data; the source keeps using this array.</param>
    /// <returns>The source.</returns>
    public static PdfSource FromBytes(byte[] data) => new(data, data.Length);

    /// <summary>Wraps only the first <paramref name="length"/> bytes of an array (for larger buffers rented from the pool).</summary>
    /// <param name="data">The data; the source keeps using this array.</param>
    /// <param name="length">Number of valid bytes; clamped to 0 … <c>data.Length</c>.</param>
    /// <returns>The source.</returns>
    public static PdfSource FromBytes(byte[] data, int length) => new(data, length);

    /// <summary>
    /// The first <paramref name="length"/> bytes as a source of their own, sharing the memory (an earlier revision of the file, without
    /// copying it); valid while this source is open. Disposing it releases nothing.
    /// </summary>
    /// <param name="length">Number of bytes; clamped to 0 … <see cref="Length"/>.</param>
    /// <returns>The shorter source.</returns>
    internal PdfSource Prefix(long length) => new(this, length);

    /// <summary>Returns the byte at a position.</summary>
    /// <param name="index">Zero-based position.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside 0 … <see cref="Length"/> − 1.</exception>
    /// <exception cref="ObjectDisposedException">The source is memory-mapped and has been disposed.</exception>
    public byte this[long index]
    {
        get
        {
            if ((ulong)index >= (ulong)Length) throw new ArgumentOutOfRangeException(nameof(index));
            if (_array is not null) return _array[index];
            if (_blocks is not null) return BlockByte(index);
            ThrowIfUnmapped();
            return _pointer[index];
        }
    }

    /// <summary>Returns the byte at a position, or -1 outside the data; for the lexer, which often looks past the end.</summary>
    /// <param name="index">Zero-based position.</param>
    /// <returns>The byte value 0–255, or -1.</returns>
    /// <exception cref="ObjectDisposedException">The source is memory-mapped and has been disposed.</exception>
    public int At(long index)
    {
        if ((ulong)index >= (ulong)Length) return -1;
        if (_array is not null) return _array[index];
        if (_blocks is not null) return BlockByte(index);
        ThrowIfUnmapped();
        return _pointer[index];
    }

    private byte BlockByte(long index)
    {
        var number = index >> BlockShift;
        var block = Volatile.Read(ref _blocks![(int)(number & (Slots - 1))]);
        if (block is null || block.Number != number) block = LoadBlock(number);
        return block.Data[(int)(index & (BlockSize - 1))];
    }

    private Block LoadBlock(long number)
    {
        var start = number << BlockShift;
        var data = new byte[(int)Math.Min(BlockSize, (_parent ?? this).Length - start)];
        ReadFile(start, data);
        var block = new Block(number, data);
        Volatile.Write(ref _blocks![(int)(number & (Slots - 1))], block);
        return block;
    }

    /// <summary>Reads from the open file; a file that is now shorter than when it was opened ends with a <see cref="PdfException"/>.</summary>
    private void ReadFile(long offset, Span<byte> target)
    {
        ThrowIfUnmapped();
        try
        {
            while (target.Length > 0)
            {
                var read = RandomAccess.Read(_file!, target, offset);
                if (read <= 0) throw new PdfException(Tr.T("Die Datei wurde von einem anderen Programm verändert und lässt sich nicht mehr vollständig lesen."));
                target = target[read..];
                offset += read;
            }
        }
        catch (IOException e)
        {
            throw new PdfException(Tr.T("Die Datei lässt sich nicht mehr lesen: {0}", e.Message));
        }
    }

    // Only for mapped sources and those read in blocks: after Dispose the pointer points to released memory, and reading it would end the
    // process (an access violation cannot be caught), the file is closed; an array stays readable
    private void ThrowIfUnmapped() => ObjectDisposedException.ThrowIf(_disposed || _parent is { _disposed: true }, this);

    /// <summary>Returns a section without copying; valid while the source is open.</summary>
    /// <param name="offset">Start position.</param>
    /// <param name="length">Requested length; shortened at the end of the data and to at most <see cref="int.MaxValue"/>.</param>
    /// <returns>The section; empty if <paramref name="offset"/> is outside the data.</returns>
    /// <exception cref="ObjectDisposedException">The source has been disposed.</exception>
    public ReadOnlySpan<byte> Span(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed || _parent is { _disposed: true }, this);
        if (offset < 0 || offset > Length) return [];
        var count = (int)Math.Min(Math.Min(length, Length - offset), int.MaxValue);
        if (_array is not null) return _array.AsSpan((int)offset, count);
        if (_blocks is null) return new ReadOnlySpan<byte>(_pointer + offset, count);
        if (count == 0) return [];
        // within one block: a view of it (the block array stays valid even if the cache replaces it); otherwise read directly, past the cache
        if (offset >> BlockShift == (offset + count - 1) >> BlockShift)
        {
            var number = offset >> BlockShift;
            var block = Volatile.Read(ref _blocks[(int)(number & (Slots - 1))]);
            if (block is null || block.Number != number) block = LoadBlock(number);
            return block.Data.AsSpan((int)(offset & (BlockSize - 1)), count);
        }
        var data = GC.AllocateUninitializedArray<byte>(count);   // filled completely by the read (large image streams: no zeroing first)
        ReadFile(offset, data);
        return data;
    }

    /// <summary>Returns a section as a separate copy.</summary>
    /// <param name="offset">Start position.</param>
    /// <param name="length">Requested length; shortened as for <see cref="Span"/>.</param>
    /// <returns>The copied bytes.</returns>
    public byte[] ReadBytes(long offset, long length) => Span(offset, length).ToArray();

    /// <summary>Finds the first occurrence of <paramref name="pattern"/> at or after <paramref name="from"/>.</summary>
    /// <param name="pattern">The bytes to search for.</param>
    /// <param name="from">Start position.</param>
    /// <param name="maxDistance">The match must lie completely within this many bytes after <paramref name="from"/>.</param>
    /// <returns>The position of the match, or -1 if there is none.</returns>
    /// <remarks>Searches in sections of 1 MiB that overlap by the pattern length.</remarks>
    public long IndexOf(ReadOnlySpan<byte> pattern, long from, long maxDistance = long.MaxValue)
    {
        const int chunk = 1 << 20;
        var end = Math.Min(Length, from + Math.Min(maxDistance, Length));
        for (var start = Math.Max(0, from); start < end; start += chunk - pattern.Length)
        {
            var span = Span(start, Math.Min(chunk, end - start));
            var hit = span.IndexOf(pattern);
            if (hit >= 0) return start + hit;
            if (start + span.Length >= end) break;
        }
        return -1;
    }

    private sealed record Search(long From, long Found);

    private readonly Search?[] _searches = new Search?[2];

    /// <summary>
    /// Like <see cref="IndexOf"/>, with a memory per <paramref name="slot"/> (always the same pattern): if the last search
    /// from <c>From</c> found the first match only at <c>Found</c> (or nothing, -1), there is no match in between, so a
    /// search from any position in that range has the same result. For repairing many streams without a matching end
    /// (otherwise quadratic).
    /// </summary>
    internal long IndexOfRemembered(ReadOnlySpan<byte> pattern, long from, int slot)
    {
        if (Volatile.Read(ref _searches[slot]) is { } last && from >= last.From && (last.Found < 0 || from <= last.Found))
            return last.Found;
        var found = IndexOf(pattern, from);
        Volatile.Write(ref _searches[slot], new Search(from, found));
        return found;
    }

    /// <summary>Finds the last occurrence of <paramref name="pattern"/> that starts before <paramref name="before"/>.</summary>
    /// <param name="pattern">The bytes to search for.</param>
    /// <param name="before">The match must start before this position.</param>
    /// <param name="maxDistance">How far back from <paramref name="before"/> to search, in bytes.</param>
    /// <returns>The position of the match, or -1 if there is none.</returns>
    public long LastIndexOf(ReadOnlySpan<byte> pattern, long before, long maxDistance)
    {
        var start = Math.Max(0, before - maxDistance);
        var span = Span(start, Math.Min(before, Length) - start + pattern.Length - 1);
        var hit = span.LastIndexOf(pattern);
        return hit < 0 ? -1 : start + hit;
    }

    /// <summary>Releases the memory mapping and closes the file, if any; calling it again has no effect.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_parent is not null) return;   // a prefix owns nothing
        if (_view is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
        }
        _map?.Dispose();
        if (_stream is not null) _stream.Dispose();
        else _file?.Dispose();
    }
}
