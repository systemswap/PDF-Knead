using System.Text;

namespace PdfKnead.Core.Pdf;

/// <summary>
/// Header line of a PDF file, such as <c>%PDF-1.7</c>. ISO 32000 places it at the start of the file; in
/// practice something sometimes precedes it (e-mail remnants, a byte order mark), so common readers search the
/// first 1024 bytes, and so does this class.
/// </summary>
public static class PdfHeader
{
    /// <summary>Number of bytes at the start of the file that are searched for the header.</summary>
    public const int SearchLength = 1024;

    private static readonly byte[] Marker = "%PDF-"u8.ToArray();

    /// <summary>Finds the version in the header line.</summary>
    /// <param name="start">The first bytes of the file; only the first <see cref="SearchLength"/> are searched.</param>
    /// <returns>The version text (for example "1.7"), or null if no header was found.</returns>
    public static string? FindVersion(ReadOnlySpan<byte> start)
    {
        var at = start[..Math.Min(start.Length, SearchLength)].IndexOf(Marker);
        if (at < 0) return null;

        var rest = start[(at + Marker.Length)..];
        var length = 0;
        while (length < rest.Length && length < 8 && (char.IsAsciiDigit((char)rest[length]) || rest[length] == '.'))
            length++;
        // At least "1.x"; without digits it is not a PDF header
        return length >= 3 && rest[1] == '.' ? Encoding.ASCII.GetString(rest[..length]) : null;
    }

    /// <summary>Reads the start of a file and finds the version in its header line.</summary>
    /// <param name="file">Path of the file.</param>
    /// <returns>The version text, or null if the file has no PDF header line.</returns>
    public static string? FindVersion(string file)
    {
        Span<byte> buffer = stackalloc byte[SearchLength];
        using var stream = File.OpenRead(file);
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return FindVersion(buffer[..read]);
    }
}
