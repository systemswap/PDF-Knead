using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Core.Pdf.IO;

/// <summary>
/// Intermediate file held only in memory (redaction, save as image, export): the writer writes into it, then it is
/// opened as a document. Nothing is created on disk that could later be recovered; a deleted temporary file would
/// remain readable in free areas of the disk. Grows in chunks of 4 MB (no copying while growing, no 2 GB limit);
/// <see cref="OpenDocument(string)"/> places the bytes contiguously in an anonymous memory area and clears the chunks.
/// </summary>
/// <remarks>Write-only and not seekable; <see cref="Position"/> always equals <see cref="Length"/>.</remarks>
public sealed class MemoryPdf : Stream
{
    private const int ChunkSize = 4 << 20;
    private readonly List<byte[]> _chunks = [];
    private long _length;

    /// <summary>False: the stream is write-only.</summary>
    public override bool CanRead => false;
    /// <summary>False: the stream cannot seek.</summary>
    public override bool CanSeek => false;
    /// <summary>True.</summary>
    public override bool CanWrite => true;
    /// <summary>Number of bytes written so far.</summary>
    public override long Length => _length;

    /// <summary>Always equal to <see cref="Length"/>; setting it throws <see cref="NotSupportedException"/>.</summary>
    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    /// <summary>Appends bytes.</summary>
    /// <param name="buffer">The source array.</param>
    /// <param name="offset">Start position in <paramref name="buffer"/>.</param>
    /// <param name="count">Number of bytes.</param>
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <summary>Appends bytes.</summary>
    /// <param name="buffer">The bytes.</param>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            var within = (int)(_length % ChunkSize);
            if (within == 0 && _length / ChunkSize == _chunks.Count) _chunks.Add(GC.AllocateUninitializedArray<byte>(ChunkSize));
            var chunk = _chunks[(int)(_length / ChunkSize)];
            var count = Math.Min(buffer.Length, ChunkSize - within);
            buffer[..count].CopyTo(chunk.AsSpan(within));
            buffer = buffer[count..];
            _length += count;
        }
    }

    /// <summary>Appends one byte.</summary>
    /// <param name="value">The byte.</param>
    public override void WriteByte(byte value) => Write([value]);

    /// <summary>Opens the written bytes as a document; the document owns them, and this buffer is empty afterwards.</summary>
    /// <param name="password">The open password if the written file is encrypted; null otherwise.</param>
    /// <returns>The open document; the caller disposes it.</returns>
    /// <exception cref="PdfPasswordException">A password is required or the given one is wrong.</exception>
    /// <exception cref="PdfException">The written bytes are not a readable PDF.</exception>
    public PdfDocument OpenDocument(string? password = null)
    {
        var source = PdfSource.FromChunks(_chunks, _length);
        Clear();
        return PdfDocument.Open(source, password);
    }

    /// <summary>Opens an encrypted written file with the writer's key (<see cref="Writing.PdfSaveResult.Security"/>); this buffer
    /// is empty afterwards.</summary>
    /// <param name="security">The security handler the file was encrypted with.</param>
    /// <returns>The open document; the caller disposes it.</returns>
    /// <exception cref="PdfException">The written bytes are not a readable PDF.</exception>
    public PdfDocument OpenDocument(Security.StandardSecurityHandler security)
    {
        var source = PdfSource.FromChunks(_chunks, _length);
        Clear();
        return PdfDocument.Open(source, security);
    }

    /// <summary>Overwrites and releases the chunks (otherwise the content would remain in memory until it is reused).</summary>
    private void Clear()
    {
        foreach (var chunk in _chunks) Array.Clear(chunk);
        _chunks.Clear();
        _length = 0;
    }

    /// <summary>Does nothing; the data is already in memory.</summary>
    public override void Flush()
    {
    }

    /// <summary>Not supported.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>Not supported.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>Not supported.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Overwrites and releases the chunks that have not been handed to a document.</summary>
    /// <param name="disposing">True when called from <see cref="Stream.Dispose()"/>.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing) Clear();
        base.Dispose(disposing);
    }
}
