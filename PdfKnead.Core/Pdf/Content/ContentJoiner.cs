using PdfKnead.Core.Pdf.Filters;

namespace PdfKnead.Core.Pdf.Content;

/// <summary>
/// The parts of /Contents joined into one array, a line end after each, for readers that keep the content as an array (font usage, the PDF/A
/// check and conversion, tagging): bounded together as one stream (<see cref="PageInterpreter.ContentPartsLimit"/>; docs/FEHLERGEDAECHTNIS.md,
/// No. 1002) and counted while it grows and as long as the array handed out is kept (<see cref="DecodedMemory"/>, No. 1003).
/// </summary>
/// <param name="parts">The number of entries in /Contents.</param>
internal sealed class ContentJoiner(int parts)
{
    private readonly long _limit = PageInterpreter.ContentPartsLimit(parts);
    private readonly DecodedWriter _all = new(0, PageInterpreter.ContentPartsLimit(parts));

    /// <summary>Adds a part and the line end after it (operators may span the parts, tokens may not).</summary>
    /// <exception cref="PdfLimitException">All parts together exceed the bound of one stream, or more is held at once than allowed.</exception>
    public void Add(ReadOnlySpan<byte> part)
    {
        _all.Write(part);
        _all.WriteByte((byte)'\n');
        if (_all.Length > _limit) throw new PdfLimitException(Tr.T("Der Seiteninhalt ist zu groß."));
    }

    /// <summary>The joined content.</summary>
    public byte[] ToArray() => _all.ToArray();
}
