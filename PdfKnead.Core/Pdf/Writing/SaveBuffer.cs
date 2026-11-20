namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Memory buffers for files that are saved in several steps in memory (written or appended, then signed): sized for the expected
/// file from the start, so that a large file is not copied again and again while the buffer grows, and handed on without a final copy
/// (<see cref="MemoryStream.GetBuffer"/> with the length).
/// </summary>
internal static class SaveBuffer
{
    /// <summary>A buffer for a file of about <paramref name="expected"/> bytes plus <paramref name="extra"/> bytes appended later.</summary>
    /// <param name="expected">Expected size of the written file (for example the size of the original).</param>
    /// <param name="extra">Room for what is appended afterwards (signatures).</param>
    /// <returns>An empty stream; if the file comes out larger, it grows as usual.</returns>
    public static MemoryStream Create(long expected, long extra = 0) =>
        new((int)Math.Clamp(expected + expected / 16 + extra + (64 << 10), 0, Array.MaxLength));

    /// <summary>The bytes written so far as a document source, without copying; valid while nothing before them changes.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <returns>The source.</returns>
    public static IO.PdfSource Source(MemoryStream buffer) => IO.PdfSource.FromBytes(buffer.GetBuffer(), (int)buffer.Length);
}
