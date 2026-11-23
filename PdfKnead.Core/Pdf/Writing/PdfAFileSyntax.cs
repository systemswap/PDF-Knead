using System.Text;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// The rules of PDF/A on the bytes of the file (ISO 19005-2, 6.1.4, 6.1.6, 6.1.7.1, 6.1.9; KONZEPT.md, section 5.21): what the parser of the
/// display reads past and therefore cannot tell — the spacing around „obj“ and „endobj“, the line ends after „stream“ and before
/// „endstream“, whether /Length is the number of bytes, hexadecimal strings in objects, the line end after „xref“. Read straight from the file,
/// object by object as the cross-references place them.
/// </summary>
internal static class PdfAFileSyntax
{
    /// <summary>The largest object read byte by byte; a stream's data is skipped by its length, not read.</summary>
    private const int MaxObjectHead = 1 << 20;

    /// <summary>Checks the file and notes each rule not met once.</summary>
    /// <param name="doc">The document (its file bytes and cross-references).</param>
    /// <param name="add">Notes a rule not met.</param>
    /// <param name="cancel">Cancels the check.</param>
    /// <param name="part">The part checked (its limits, and the cross-references PDF/A-1 asks for).</param>
    public static void Check(PdfDocument doc, Action<PdfARule, string> add, CancellationToken cancel, PdfAConformance part)
    {
        var limits = part.Limits();
        var count = 0;
        foreach (var (number, position) in doc.StoredObjects.OrderBy(o => o.Position))
        {
            if ((++count & 1023) == 0) cancel.ThrowIfCancellationRequested();
            if (position <= 0 || position >= doc.FileSize) continue;
            Object(doc, number, position, add, limits);
        }
        CrossReferences(doc, add, part.Part() == 1);
        // every part: a linearized file needs /ID in the trailer of its first page as well (No. 498; the comparison of both is part 1 only)
        TrailerIds(doc, add);
    }

    private static bool Eol(byte b) => b is 10 or 13;

    private static bool White(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool Delimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    /// <summary>The bytes of the file from a position on (at most <paramref name="length"/>).</summary>
    private static ReadOnlySpan<byte> At(PdfDocument doc, long position, long length) =>
        doc.FileBytes(position, Math.Max(0, Math.Min(length, doc.FileSize - position)));

    /// <summary>One object stored in the file: „n g obj“ … „endobj“, with its stream if it has one.</summary>
    private static void Object(PdfDocument doc, int number, long position, Action<PdfARule, string> add, PdfALimits limits)
    {
        // a position on the white-space before the object is read past (by strict readers as well): the object starts after it
        var lead = At(doc, position, 64);
        var skip = 0;
        while (skip < lead.Length && White(lead[skip])) skip++;
        position += skip;
        var head = At(doc, position, MaxObjectHead);
        // the object number is preceded by an end-of-line marker; number, generation and obj separated by one white-space; obj followed by one
        if (position > 0 && !Eol(doc.FileBytes(position - 1, 1)[0]))
            add(PdfARules.ObjectSpacing, Tr.T("Vor einem Objekt fehlt das Zeilenende."));
        var i = 0;
        while (i < head.Length && head[i] is >= (byte)'0' and <= (byte)'9') i++;
        if (i == 0 || i >= head.Length || head[i] != ' ' || i + 1 >= head.Length || !char.IsAsciiDigit((char)head[i + 1]))
        {
            add(PdfARules.ObjectSpacing, Tr.T("Objektnummer und Generation sind nicht durch genau ein Leerzeichen getrennt."));
            return;
        }
        i++;
        while (i < head.Length && head[i] is >= (byte)'0' and <= (byte)'9') i++;
        if (i + 4 > head.Length || head[i] != ' ' || !head[(i + 1)..].StartsWith("obj"u8))
        {
            add(PdfARules.ObjectSpacing, Tr.T("Generation und „obj“ sind nicht durch genau ein Leerzeichen getrennt."));
            return;
        }
        i += 4;
        if (i >= head.Length || !Eol(head[i])) add(PdfARules.ObjectSpacing, Tr.T("Nach „obj“ fehlt das Zeilenende."));

        // the body up to „stream“ or „endobj“: hexadecimal strings, strings and comments skipped as the lexer does
        var end = Body(head, i, add, out var keyword, limits);
        if (keyword == "stream")
        {
            var data = end;
            if (data < head.Length && head[data] == '\n') data++;
            else if (data + 1 < head.Length && head[data] == '\r' && head[data + 1] == '\n') data += 2;
            else add(PdfARules.StreamKeywords, Tr.T("Nach „stream“ steht nicht CR LF oder LF."));
            Stream(doc, number, position + data, add);
            return;
        }
        if (keyword == "endobj")
        {
            var at = end - "endobj".Length;
            if (at == 0 || !Eol(head[at - 1])) add(PdfARules.ObjectSpacing, Tr.T("Vor „endobj“ fehlt das Zeilenende."));
            if (end < head.Length && !Eol(head[end])) add(PdfARules.ObjectSpacing, Tr.T("Nach „endobj“ fehlt das Zeilenende."));
        }
    }

    /// <summary>
    /// Reads an object's body from <paramref name="from"/> to the keyword „stream“ or „endobj“ (<paramref name="keyword"/>; null if neither
    /// follows) and returns the position after it; checks the hexadecimal strings on the way (6.1.6).
    /// </summary>
    private static int Body(ReadOnlySpan<byte> data, int from, Action<PdfARule, string> add, out string? keyword, PdfALimits limits)
    {
        keyword = null;
        for (var i = from; i < data.Length; i++)
        {
            switch (data[i])
            {
                case (byte)'%':
                    while (i < data.Length && !Eol(data[i])) i++;
                    break;
                case (byte)'(':
                    var depth = 1;
                    for (i++; i < data.Length && depth > 0; i++)
                        if (data[i] == '\\') i++;
                        else if (data[i] == '(') depth++;
                        else if (data[i] == ')') depth--;
                    i--;
                    break;
                case (byte)'<' when i + 1 < data.Length && data[i + 1] == '<':
                    i++;
                    break;
                case (byte)'<':
                    var digits = 0;
                    var other = false;
                    for (i++; i < data.Length && data[i] != '>'; i++)
                        if (Uri.IsHexDigit((char)data[i])) digits++;
                        else if (!White(data[i])) other = true;
                    if (other) add(PdfARules.HexDigits, Tr.T("Eine hexadezimale Zeichenkette enthält andere Zeichen als Ziffern."));
                    if (digits % 2 != 0) add(PdfARules.HexEven, Tr.T("Eine hexadezimale Zeichenkette hat eine ungerade Zahl von Ziffern."));
                    break;
                case (byte)'/':
                    // a name: its characters are no numbers
                    while (i + 1 < data.Length && !White(data[i + 1]) && !Delimiter(data[i + 1])) i++;
                    break;
                case (byte)'+' or (byte)'-' or (byte)'.' or >= (byte)'0' and <= (byte)'9' when i == 0 || White(data[i - 1]) || Delimiter(data[i - 1]):
                    // numbers as written (the parser rounds what lies beyond its precision, 6.1.13)
                    var start = i;
                    while (i + 1 < data.Length && !White(data[i + 1]) && !Delimiter(data[i + 1])) i++;
                    var faults = ContentConformance.Number(data[start..(i + 1)], limits.Real);
                    if (faults.HasFlag(ContentFaults.IntegerOutOfRange)) add(PdfARules.IntegerRange, Tr.T("Eine Zahl liegt außerhalb der Grenzen."));
                    if (faults.HasFlag(ContentFaults.RealOutOfRange)) add(PdfARules.RealRange, Tr.T("Eine Kommazahl liegt außerhalb der Grenzen."));
                    if (faults.HasFlag(ContentFaults.RealNearZero)) add(PdfARules.RealNearZero, Tr.T("Eine Kommazahl liegt näher an null als erlaubt."));
                    break;
                case (byte)'s' when Keyword(data, i, "stream"):
                    keyword = "stream";
                    return i + 6;
                case (byte)'e' when Keyword(data, i, "endobj"):
                    keyword = "endobj";
                    return i + 6;
            }
        }
        return data.Length;
    }

    /// <summary>The keyword stands at <paramref name="at"/> as a token of its own.</summary>
    private static bool Keyword(ReadOnlySpan<byte> data, int at, string word)
    {
        if (!data[at..].StartsWith(Encoding.ASCII.GetBytes(word))) return false;
        // (after „/“ it is part of a name)
        var before = at == 0 || White(data[at - 1]) || Delimiter(data[at - 1]) && data[at - 1] != '/';
        var after = at + word.Length >= data.Length || White(data[at + word.Length]) || Delimiter(data[at + word.Length]);
        return before && after;
    }

    /// <summary>
    /// A stream's data from <paramref name="start"/> on: /Length is the number of bytes up to the end-of-line marker before „endstream“
    /// (6.1.7.1). Where /Length does not lead to „endstream“, the real length is where the keyword first follows.
    /// </summary>
    private static void Stream(PdfDocument doc, int number, long start, Action<PdfARule, string> add)
    {
        long? length = doc.GetObject(number) is PdfStream stream ? stream.Dictionary.GetInteger(PdfName.Length) : null;
        if (length is { } n && n >= 0 && start + n <= doc.FileSize && EndsAt(doc, start + n, out var eolBefore))
        {
            if (eolBefore) return;
            // „endstream“ right after the data: if the data end in an end-of-line marker, /Length counted it (the real length is one less)
            if (n > 0 && Eol(doc.FileBytes(start + n - 1, 1)[0])) add(PdfARules.StreamLength, Tr.T("Die Länge eines Datenstroms (/Length) stimmt nicht."));
            else add(PdfARules.StreamKeywords, Tr.T("Vor „endstream“ fehlt das Zeilenende."));
            return;
        }
        add(PdfARules.StreamLength, Tr.T("Die Länge eines Datenstroms (/Length) stimmt nicht."));
    }

    /// <summary>„endstream“ follows at <paramref name="at"/> after one end-of-line marker (<paramref name="eolBefore"/>) or directly.</summary>
    private static bool EndsAt(PdfDocument doc, long at, out bool eolBefore)
    {
        var tail = At(doc, at, 12);
        eolBefore = true;
        if (tail.StartsWith("\r\nendstream"u8) || tail.StartsWith("\nendstream"u8) || tail.StartsWith("\rendstream"u8)) return true;
        eolBefore = false;
        return tail.StartsWith("endstream"u8);
    }

    /// <summary>Each classic cross-reference section from „startxref“ back along /Prev: „xref“ followed by one end-of-line marker (6.1.4).</summary>
    /// <summary>
    /// The cross-reference sections from the last on (by /Prev): „xref“ followed by one end-of-line marker; in PDF/A-1 (ISO 19005-1, 6.1.4)
    /// no cross-reference streams, also not those of a hybrid file (/XRefStm), and each subsection header „first count“ with a single space.
    /// </summary>
    private static void CrossReferences(PdfDocument doc, Action<PdfARule, string> add, bool one)
    {
        var tailLength = (int)Math.Min(doc.FileSize, 1024);
        var tail = Encoding.Latin1.GetString(doc.FileBytes(doc.FileSize - tailLength, tailLength));
        var at = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        if (at < 0 || !long.TryParse(tail[(at + 9)..].Trim().Split('\n', '\r')[0].Trim(), out var section)) return;
        var header = HeaderOffset(doc);
        var seen = new HashSet<long>();
        while (section >= 0 && section + header < doc.FileSize && seen.Add(section) && seen.Count < 1000)
        {
            var bytes = At(doc, section + header, 64 * 1024);
            if (!bytes.StartsWith("xref"u8))
            {
                // a cross-reference stream: nothing of the table's kind to check
                if (one) add(PdfARules.NoXrefStream, Tr.T("Die Datei hat Querverweise als Datenstrom (Xref-Stream)."));
                return;
            }
            var i = 4;
            var eol = 0;
            if (i < bytes.Length && bytes[i] == '\r') { i++; eol++; }
            if (i < bytes.Length && bytes[i] == '\n') { i++; eol++; }
            if (eol == 0 || i >= bytes.Length || !char.IsAsciiDigit((char)bytes[i]))
                add(PdfARules.XrefLineEnd, Tr.T("Nach „xref“ steht nicht genau ein Zeilenende vor dem Abschnitt."));
            var trailerAt = one ? Subsections(doc, section + header + i, add) : -1;
            var text = trailerAt > 0 ? Encoding.Latin1.GetString(At(doc, trailerAt, 64 * 1024)) : Encoding.Latin1.GetString(bytes);
            var trailer = text.IndexOf("trailer", StringComparison.Ordinal);
            if (trailer < 0) return;
            if (one && System.Text.RegularExpressions.Regex.IsMatch(text[trailer..Math.Min(text.Length, trailer + 4096)], @"/XRefStm[\s/]"))
                add(PdfARules.NoXrefStream, Tr.T("Die Datei hat Querverweise als Datenstrom (Xref-Stream)."));
            var prev = System.Text.RegularExpressions.Regex.Match(text[trailer..], @"/Prev\s+(\d+)");
            if (!prev.Success || !long.TryParse(prev.Groups[1].Value, out section)) return;
        }
    }

    /// <summary>
    /// The subsections of a cross-reference table from <paramref name="position"/> (after „xref“ and its line end): each header „first count“
    /// with a single space (ISO 19005-1, 6.1.4), followed by its entries of 20 bytes each.
    /// </summary>
    /// <returns>Where the trailer starts; -1 where the table cannot be followed.</returns>
    private static long Subsections(PdfDocument doc, long position, Action<PdfARule, string> add)
    {
        for (var n = 0; n < 1_000_000 && position < doc.FileSize; n++)
        {
            var line = At(doc, position, 64);
            var end = line.IndexOfAny((byte)'\r', (byte)'\n');
            if (line.StartsWith("trailer"u8)) return position;
            if (end < 0) return -1;
            var header = Encoding.Latin1.GetString(line[..end]);
            var match = System.Text.RegularExpressions.Regex.Match(header, @"^(\d+)([ \t\f\0]+)(\d+)[ \t\f\0]*$");
            // something else than a header: the table is not one strict readers follow either; other rules speak of it
            if (!match.Success || !long.TryParse(match.Groups[3].Value, out var count) || count < 0) return -1;
            if (match.Groups[2].Value != " ")
            {
                add(PdfARules.XrefSubsections, Tr.T("Im Kopf eines Querverweis-Abschnitts stehen erste Nummer und Anzahl nicht durch genau ein Leerzeichen getrennt."));
                return -1;
            }
            position += end;
            while (position < doc.FileSize && doc.FileBytes(position, 1)[0] is 10 or 13) position++;
            position += count * 20;
        }
        return -1;
    }

    /// <summary>
    /// The /ID of the trailers as ISO 19005-1, 6.1.3 asks: in the last trailer — in a linearized file (ISO 32000-1, Annex F) in the trailer of
    /// its first page, and where both have one, the same in both. (The document's trailer merges the trailers of all sections and cannot tell.)
    /// </summary>
    private static void TrailerIds(PdfDocument doc, Action<PdfARule, string> add)
    {
        var tailLength = Math.Min(doc.FileSize, 64 * 1024);
        var tailBytes = At(doc, doc.FileSize - tailLength, tailLength).ToArray();
        var last = Encoding.Latin1.GetString(tailBytes).LastIndexOf("trailer", StringComparison.Ordinal);
        var head = At(doc, 0, 1 << 20).ToArray();
        var text = Encoding.Latin1.GetString(head);
        var first = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)[ \t\r\n]+(\d+)[ \t\r\n]+obj");
        var source = Pdf.IO.PdfSource.FromBytes(head);
        try
        {
            // without a „trailer“ the cross-references are a stream, which PDF/A-1 does not allow (another rule)
            var lastTrailer = last < 0 ? null : Trailer(Pdf.IO.PdfSource.FromBytes(tailBytes), last + 7);
            // linearized: the first object is the linearization dictionary, and its /L the length of the file (ISO 32000-1, F.2.2) — after an
            // incremental update the file is no longer read as linearized
            // and it lies wholly within the first 1 024 bytes of the file (ISO 32000-1, F.2.2; ISO 32000-2, F.3.3) — before a dictionary
            // further on still counted, and the comparison of both /ID applied to a file that is not linearized (M3, Annex F, 23.11.2026)
            var end = first.Success ? text.IndexOf("endobj", first.Index, StringComparison.Ordinal) : -1;
            var linearized = first.Success && end >= 0 && end + 6 <= 1024 && new Pdf.Parsing.PdfParser(new Pdf.Parsing.PdfLexer(source)).ParseIndirectObject(first.Index)?.Value is PdfDictionary
                linearization && linearization.ContainsKey(PdfName.Get("Linearized")) && linearization.GetInteger(PdfName.Get("L")) == doc.FileSize;
            var firstAt = linearized ? text.IndexOf("trailer", first.Index, StringComparison.Ordinal) : -1;
            var firstPage = firstAt < 0 ? null : Trailer(source, firstAt + 7);
            // from PDF 1.5 the cross-references of the first page may be a stream: its dictionary is the trailer (the object after the
            // linearization dictionary)
            if (linearized && firstPage is null && first.NextMatch() is { Success: true } second &&
                new Pdf.Parsing.PdfParser(new Pdf.Parsing.PdfLexer(source)).ParseIndirectObject(second.Index)?.Value is PdfStream xref &&
                xref.Dictionary.GetName(PdfName.Type)?.Value == "XRef")
                firstPage = xref.Dictionary;
            if (linearized && firstPage is not null && firstPage.Get(PdfName.ID) is not PdfArray)
                add(PdfARules.FileId, Tr.T("Der Trailer der ersten Seite hat keine Kennung (/ID)."));
            else if (!linearized && lastTrailer is not null && lastTrailer.Get(PdfName.ID) is not PdfArray)
                add(PdfARules.FileId, Tr.T("Der letzte Trailer hat keine Kennung (/ID)."));
            if (firstPage?.Get(PdfName.ID) is PdfArray a && lastTrailer?.Get(PdfName.ID) is PdfArray b && !SameId(a, b))
                add(PdfARules.FileIdLinearized, Tr.T("Die Kennung (/ID) im Trailer der ersten Seite und im letzten Trailer sind verschieden."));
        }
        catch (PdfException)
        {
            // an unreadable head: the parser's rules speak of it
        }
    }

    private static PdfDictionary? Trailer(Pdf.IO.PdfSource source, long position) =>
        new Pdf.Parsing.PdfParser(new Pdf.Parsing.PdfLexer(source, position)).ParseObject() as PdfDictionary;

    private static bool SameId(PdfArray a, PdfArray b) =>
        a.Count == b.Count && Enumerable.Range(0, a.Count).All(k => a.Get(k) is PdfString x && b.Get(k) is PdfString y && x.Bytes.AsSpan().SequenceEqual(y.Bytes));

    /// <summary>The bytes before the „%PDF-“ header (positions in the cross-references count from it).</summary>
    private static long HeaderOffset(PdfDocument doc)
    {
        var head = At(doc, 0, 1024);
        var at = head.IndexOf("%PDF-"u8);
        return Math.Max(0, at);
    }
}
