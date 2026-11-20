using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.IO;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Parsing;

/// <summary>Type of a cross-reference entry.</summary>
public enum XRefEntryType : byte
{
    /// <summary>Free entry; the object does not exist.</summary>
    Free,
    /// <summary>The object lies directly in the file at <see cref="XRefEntry.Offset"/>.</summary>
    InFile,
    /// <summary>The object lies in object stream number <see cref="XRefEntry.Offset"/> at index <see cref="XRefEntry.Generation"/>.</summary>
    Compressed,
}

/// <summary>A cross-reference entry.</summary>
/// <param name="Type">The entry type.</param>
/// <param name="Offset">For <see cref="XRefEntryType.InFile"/>, the byte position of the object; for
/// <see cref="XRefEntryType.Compressed"/>, the number of the object stream.</param>
/// <param name="Generation">For <see cref="XRefEntryType.InFile"/>, the generation number; for
/// <see cref="XRefEntryType.Compressed"/>, the index within the object stream.</param>
public readonly record struct XRefEntry(XRefEntryType Type, long Offset, int Generation);

/// <summary>Cross-references: where each object lies, plus the (merged) trailer.</summary>
/// <remarks>Not thread-safe.</remarks>
public sealed class XRefTable
{
    /// <summary>Entries by object number.</summary>
    public Dictionary<int, XRefEntry> Entries { get; } = [];

    /// <summary>The trailer dictionary.</summary>
    public PdfDictionary Trailer { get; set; } = new();

    /// <summary>
    /// The trailer of the newest section as read, before older ones were merged into <see cref="Trailer"/> (an appended section carries
    /// all its entries on, ISO 32000-2, 7.5.6); <see langword="null"/> after a rebuild.
    /// </summary>
    public PdfDictionary? NewestTrailer { get; internal set; }

    /// <summary>True if the table was obtained by scanning the file instead of being read.</summary>
    public bool Rebuilt { get; init; }

    /// <summary>
    /// Offset of the newest section as stated after „startxref“ (what an appended section names as /Prev); -1 if unknown,
    /// for example after a rebuild.
    /// </summary>
    public long NewestSection { get; internal set; } = -1;

    /// <summary>Whether the newest section is a cross-reference stream (otherwise a classic table).</summary>
    public bool NewestIsStream { get; internal set; }

    /// <summary>After a rebuild: the object streams found, whose contents still have to be registered.</summary>
    public List<int> ObjectStreams { get; } = [];

    /// <summary>
    /// Most entries a table may hold: the object numbers of ISO 32000-2, Annex C (8,388,607 indirect objects), and no more than the memory budget
    /// of the machine allows (64 bytes per entry within at most 512 MB; docs/FEHLERGEDAECHTNIS.md, No. 744).
    /// </summary>
    internal static int MaxEntries => (int)Math.Min(PdfLimits.MaxObjectNumber + 1L, PdfLimits.Budget(512L << 20) / 64);

    /// <summary>Sets an entry only if it is still missing; newer sections are read first.</summary>
    /// <exception cref="PdfLimitException">More entries than <see cref="MaxEntries"/>: the table is not believed, the file gets rebuilt.</exception>
    internal void AddIfMissing(int number, XRefEntry entry)
    {
        if (Entries.Count >= MaxEntries && !Entries.ContainsKey(number))
            throw new PdfLimitException(Tr.T("Mehr Querverweise als Objekte möglich ({0}).", MaxEntries));
        Entries.TryAdd(number, entry);
    }

    // free entries of the oldest section: they hide nothing, but they say the object is deleted (null, ISO 32000-2, 7.5.4) rather than missing
    // (which would start a repair) — kept as bits, at most 1 MB for all numbers of Annex C, instead of millions of entries (No. 744)
    private System.Collections.BitArray? _free;

    /// <summary>Bytes of cross-reference streams decoded while reading the table (No. 749).</summary>
    internal long DecodedBytes { get; set; }

    /// <summary>The highest number marked free by <see cref="MarkFree"/>; -1 if none.</summary>
    internal int HighestFree { get; private set; } = -1;

    /// <summary>Notes a free entry of the oldest section, unless a newer section already has an entry for the number.</summary>
    /// <param name="number">The object number, at most <see cref="PdfLimits.MaxObjectNumber"/>.</param>
    internal void MarkFree(int number)
    {
        if (number is < 0 or > PdfLimits.MaxObjectNumber || Entries.ContainsKey(number)) return;
        if (_free is null) _free = new System.Collections.BitArray(Math.Max(1024, number + 1));
        else if (number >= _free.Length) _free.Length = (int)Math.Min(PdfLimits.MaxObjectNumber + 1L, Math.Max(number + 1L, _free.Length * 2L));
        _free[number] = true;
        if (number > HighestFree) HighestFree = number;
    }

    /// <summary>Whether the cross-references mark the object as deleted (a free entry), as opposed to having no entry for it.</summary>
    /// <param name="number">The object number.</param>
    /// <returns>True for a free entry.</returns>
    public bool IsFree(int number) => Entries.TryGetValue(number, out var entry)
        ? entry.Type == XRefEntryType.Free
        : _free is { } free && number >= 0 && number < free.Length && free[number];

    /// <summary>Takes over the trailer keys of an older section that the newer one does not have.</summary>
    internal void MergeTrailer(PdfDictionary older)
    {
        foreach (var (key, value) in older.Entries)
            if (!Trailer.ContainsKey(key)) Trailer.Set(key, value);
    }
}

/// <summary>
/// Reads the cross-references starting at "startxref": classic tables, cross-reference streams (PDF 1.5), hybrid
/// files (/XRefStm) and the /Prev chain of incremental updates. Throws <see cref="PdfException"/> if anything is
/// wrong; <see cref="XRefRebuilder"/> then rebuilds the table from the file.
/// </summary>
public static class XRefReader
{
    private static readonly byte[] StartXRef = "startxref"u8.ToArray();

    /// <summary>Reads the cross-references of a file.</summary>
    /// <param name="source">The file contents.</param>
    /// <param name="headerOffset">Bytes before "%PDF-"; some files count their offsets without them.</param>
    /// <returns>The table with the trailer of the newest section, completed by older ones.</returns>
    /// <exception cref="PdfException">"startxref" is missing within the last 64 KiB, a section is not at the stated
    /// position or damaged, or the trailer has no /Root.</exception>
    /// <remarks>A loop in /Prev or more than <see cref="PdfLimits.MaxXRefSections"/> sections end the chain.</remarks>
    public static XRefTable Read(PdfSource source, long headerOffset)
    {
        var at = source.LastIndexOf(StartXRef, source.Length, 1 << 16);
        if (at < 0) throw new PdfException(Tr.T("„startxref“ fehlt."));

        var lexer = new PdfLexer(source, at + StartXRef.Length);
        var token = lexer.Next();
        if (token.Kind != PdfTokenKind.Integer || token.Integer < 0)
            throw new PdfException(Tr.T("Nach „startxref“ steht keine Position."));

        var table = new XRefTable();
        var visited = new HashSet<long>();
        long? next = token.Integer;
        var first = true;
        while (next is { } offset)
        {
            if (visited.Count >= PdfLimits.MaxXRefSections || !visited.Add(offset)) break;   // loop in /Prev
            offset = Locate(source, offset, headerOffset);
            var trailer = ReadSection(source, offset, table, headerOffset);
            if (first)
            {
                table.Trailer = trailer;
                table.NewestTrailer = Copy(trailer);
                table.NewestSection = token.Integer;
                table.NewestIsStream = !IsTableAt(source, offset);
                first = false;
            }
            else
            {
                table.MergeTrailer(trailer);
            }
            next = trailer.GetRaw(PdfName.Prev) is PdfInteger { Value: >= 0 } prev ? prev.Value : null;
        }

        if (table.Trailer.GetRaw(PdfName.Root) is null)
            throw new PdfException(Tr.T("Im Trailer fehlt /Root."));
        return table;
    }

    private static PdfDictionary Copy(PdfDictionary dict)
    {
        var copy = new PdfDictionary(dict.Count);
        foreach (var (key, value) in dict.Entries) copy.Set(key, value);
        return copy;
    }

    /// <summary>
    /// Checks the offset: "xref" or "n g obj" must stand there. Offsets count from the "%" of "%PDF-" (ISO 32000-2, 7.5.2), so with bytes
    /// before the header that comes first; then, as some writers count them, from the start of the file (No. 747).
    /// </summary>
    private static long Locate(PdfSource source, long offset, long headerOffset)
    {
        if (headerOffset > 0 && StartsSection(source, offset + headerOffset)) return offset + headerOffset;
        if (StartsSection(source, offset)) return offset;
        throw new PdfException(Tr.T("Querverweise nicht an der angegebenen Stelle."));
    }

    private static bool IsTableAt(PdfSource source, long offset)
    {
        var lexer = new PdfLexer(source, offset);
        lexer.SkipWhitespace();
        return lexer.IsKeywordAt(lexer.Position, "xref");
    }

    private static bool StartsSection(PdfSource source, long offset)
    {
        if (offset < 0 || offset >= source.Length) return false;
        var lexer = new PdfLexer(source, offset);
        lexer.SkipWhitespace();
        if (lexer.IsKeywordAt(lexer.Position, "xref")) return true;
        var a = lexer.Next();
        var b = lexer.Next();
        var c = lexer.Next();
        return a.Kind == PdfTokenKind.Integer && b.Kind == PdfTokenKind.Integer && c.IsKeyword("obj");
    }

    private static PdfDictionary ReadSection(PdfSource source, long offset, XRefTable table, long headerOffset)
    {
        var lexer = new PdfLexer(source, offset);
        lexer.SkipWhitespace();
        if (!lexer.IsKeywordAt(lexer.Position, "xref"))
            return ReadStreamSection(source, offset, table);

        lexer.Next();
        ReadTable(lexer, table);
        var trailerToken = lexer.Next();
        if (!trailerToken.IsKeyword("trailer"))
            throw new PdfException(Tr.T("Nach der Querverweistabelle fehlt „trailer“."));
        if (new PdfParser(lexer).ParseObject() is not PdfDictionary trailer)
            throw new PdfException(Tr.T("Trailer ist kein Dictionary."));

        // Hybrid file: objects in object streams are listed in an additional cross-reference stream
        if (trailer.GetRaw(PdfName.XRefStm) is PdfInteger { Value: >= 0 } stm)
        {
            try
            {
                // the stream belongs to this section: its free entries hide older ones if the section has older ones (its /Prev stands in
                // the trailer of the table, the stream itself has none in a hybrid file; 7.5.8.4)
                ReadStreamSection(source, Locate(source, stm.Value, headerOffset), table, olderSections: trailer.ContainsKey(PdfName.Prev));
            }
            catch (PdfException)
            {
                // Only a supplement; without it, at most objects are missing that the repair finds later
            }
        }
        return trailer;
    }

    /// <summary>Classic table: subsections "start count", then "offset generation n|f" per object.</summary>
    private static void ReadTable(PdfLexer lexer, XRefTable table)
    {
        var firstSection = true;
        while (true)
        {
            var position = lexer.Position;
            var startToken = lexer.Next();
            if (startToken.Kind != PdfTokenKind.Integer)
            {
                lexer.Position = position;
                return;
            }
            var countToken = lexer.Next();
            if (countToken.Kind != PdfTokenKind.Integer || countToken.Integer < 0)
                throw new PdfException(Tr.T("Querverweistabelle kaputt."));

            var start = startToken.Integer;
            for (long i = 0; i < countToken.Integer; i++)
            {
                var offsetToken = lexer.Next();
                var genToken = lexer.Next();
                var typeToken = lexer.Next();
                if (offsetToken.Kind != PdfTokenKind.Integer || genToken.Kind != PdfTokenKind.Integer || typeToken.Kind != PdfTokenKind.Keyword)
                    throw new PdfException(Tr.T("Querverweistabelle kaputt."));

                // Common error: the first subsection starts at 1 although the entry for object 0 is present
                if (firstSection && i == 0 && start == 1 && typeToken.IsKeyword("f") && genToken.Integer == 65535)
                    start = 0;

                var number = start + i;
                if (number is < 0 or > PdfLimits.MaxObjectNumber) continue;
                var entry = typeToken.IsKeyword("n")
                    ? new XRefEntry(XRefEntryType.InFile, offsetToken.Integer, (int)Math.Clamp(genToken.Integer, 0, 65535))
                    : new XRefEntry(XRefEntryType.Free, 0, 0);
                // Offset 0 for "n" is nonsense (the header is there): treat it as free
                if (entry.Type == XRefEntryType.InFile && entry.Offset <= 0) entry = new XRefEntry(XRefEntryType.Free, 0, 0);
                table.AddIfMissing((int)number, entry);
            }
            firstSection = false;
        }
    }

    /// <summary>Cross-reference stream (7.5.8): /W, /Index, entries of type 0/1/2.</summary>
    /// <param name="olderSections">Whether older sections follow this one (free entries hide theirs); null: the stream's own /Prev says so.</param>
    private static PdfDictionary ReadStreamSection(PdfSource source, long offset, XRefTable table, bool? olderSections = null)
    {
        var parsed = new PdfParser(new PdfLexer(source)).ParseIndirectObject(offset);
        if (parsed?.Value is not PdfStream stream || !stream.Dictionary.IsType(PdfName.XRef))
            throw new PdfException(Tr.T("Kein Querverweis-Stream an der angegebenen Stelle."));

        var dict = stream.Dictionary;
        // a damaged stream is no cross-reference section: its entries end where the damage begins, and the objects behind it would be
        // missing without a word — then the cross-references count as unreadable and are rebuilt from the file, as for any other damage
        // (7.5.8; S-A A06; docs/FEHLERGEDAECHTNIS.md, No. 797 — a file of a public test collection showed no page, other readers its one)
        var decoded = StreamFilters.Decode(source.ReadBytes(stream.DataOffset, stream.DataLength), dict);
        if (decoded.Truncated) throw new PdfException(Tr.T("Querverweis-Stream beschädigt."));
        var data = decoded.Data;
        // all sections together: no more than one stream may decode to, or 64 times the file — streams whose data overlap could otherwise
        // have the same decompression bomb decoded once per section (docs/FEHLERGEDAECHTNIS.md, No. 749); then the table is not believed
        table.DecodedBytes += data.Length;
        if (table.DecodedBytes > Math.Max(PdfLimits.MaxDecodedStreamBytes, 64 * source.Length))
            throw new PdfLimitException(Tr.T("Querverweis-Ströme entpacken zusammen zu groß ({0} MB).", table.DecodedBytes >> 20));

        var w = dict.GetArray(PdfName.W);
        if (w is null || w.Count < 3) throw new PdfException(Tr.T("Querverweis-Stream ohne gültiges /W."));
        int w1 = Width(w, 0), w2 = Width(w, 1), w3 = Width(w, 2);
        var entryLength = w1 + w2 + w3;
        if (entryLength == 0) throw new PdfException(Tr.T("Querverweis-Stream ohne gültiges /W."));

        var size = dict.GetInteger(PdfName.Size) ?? 0;
        var index = dict.GetArray(PdfName.Index);
        var ranges = new List<(long Start, long Count)>();
        if (index is { Count: >= 2 })
        {
            for (var i = 0; i + 1 < index.Count; i += 2)
                ranges.Add(((long)(index.GetNumber(i) ?? 0), (long)(index.GetNumber(i + 1) ?? 0)));
        }
        else
        {
            ranges.Add((0, size));
        }

        // a free entry only hides the entry of an older section: without one there is nothing to hide (No. 744)
        var hidesOlder = olderSections ?? dict.ContainsKey(PdfName.Prev);
        long pos = 0;
        foreach (var (start, count) in ranges)
        {
            // object numbers beyond ISO 32000-2, Annex C do not exist: their entries are passed over in one step (a decompression bomb of
            // free entries took 2 GB from a file of 114 KB; docs/FEHLERGEDAECHTNIS.md, No. 744)
            var usable = start > PdfLimits.MaxObjectNumber || count <= 0 ? 0 : Math.Min(count, PdfLimits.MaxObjectNumber + 1L - Math.Max(0, start));
            for (long i = 0; i < usable && pos + entryLength <= data.Length; i++, pos += entryLength)
            {
                var at = (int)pos;
                var type = w1 == 0 ? 1 : Field(data, at, w1);
                var f2 = Field(data, at + w1, w2);
                var f3 = Field(data, at + w1 + w2, w3);
                var number = start + i;
                if (number is < 0 or > int.MaxValue) continue;
                var entry = type switch
                {
                    1 when f2 > 0 => new XRefEntry(XRefEntryType.InFile, f2, (int)Math.Clamp(f3, 0, 65535)),
                    2 => new XRefEntry(XRefEntryType.Compressed, f2, (int)Math.Clamp(f3, 0, int.MaxValue)),
                    _ => new XRefEntry(XRefEntryType.Free, 0, 0),
                };
                if (entry.Type == XRefEntryType.Free && !hidesOlder)
                {
                    table.MarkFree((int)number);
                    continue;
                }
                table.AddIfMissing((int)number, entry);
            }
            if (count > usable) pos += (count - usable) * entryLength;
            if (pos >= data.Length) break;
        }
        return dict;
    }

    private static int Width(PdfArray w, int i) => (int)Math.Clamp(w.GetNumber(i) ?? 0, 0, 8);

    private static long Field(byte[] data, int pos, int width)
    {
        long value = 0;
        for (var i = 0; i < width; i++) value = (value << 8) | data[pos + i];
        return value;
    }
}

/// <summary>
/// Rebuilds the cross-references when they are missing or wrong: searches the whole file for "n g obj", takes the
/// last occurrence of numbers that appear several times (later updates), collects trailers and cross-reference
/// streams for /Root, /Info, /Encrypt, /ID and /Size, and records the object streams (the document registers their
/// contents afterwards).
/// </summary>
public static class XRefRebuilder
{
    private static readonly byte[] Obj = "obj"u8.ToArray();
    private static readonly byte[] Trailer = "trailer"u8.ToArray();

    /// <summary>Rebuilds the cross-references by scanning the file.</summary>
    /// <param name="source">The file contents.</param>
    /// <returns>The rebuilt table, with <see cref="XRefTable.Rebuilt"/> set; empty if no object was found.</returns>
    /// <remarks>Reads the whole file twice; the cost grows with the file size.</remarks>
    public static XRefTable Rebuild(PdfSource source)
    {
        var table = new XRefTable { Rebuilt = true };
        var objectStreams = new List<int>();
        PdfDictionary? lastTrailer = null;
        // every dictionary merged into the trailer, in that order (to fall back on an older one whose /Root is a catalogue, No. 813)
        var merged = new List<PdfDictionary>();

        // 1. All "n g obj"
        for (var at = source.IndexOf(Obj, 0); at >= 0; at = source.IndexOf(Obj, at + Obj.Length))
        {
            if (PdfLexer.IsRegular(source.At(at + 3)) || (at > 0 && PdfLexer.IsRegular(source.At(at - 1)) && !IsDigit(source.At(at - 1))))
                continue;
            if (FindObjectStart(source, at) is not { } start) continue;
            var parser = new PdfParser(new PdfLexer(source));
            var lexer = new PdfLexer(source, start);
            var number = lexer.Next();
            var generation = lexer.Next();
            if (number.Integer is < 0 or > int.MaxValue) continue;
            table.Entries[(int)number.Integer] = new XRefEntry(XRefEntryType.InFile, start, (int)Math.Clamp(generation.Integer, 0, 65535));

            // Cross-reference streams carry trailer keys, object streams carry further objects
            try
            {
                var parsed = parser.ParseIndirectObject(start);
                if (parsed?.Value is PdfStream s)
                {
                    if (s.Dictionary.IsType(PdfName.XRef))
                    {
                        lastTrailer = Merge(lastTrailer, s.Dictionary);
                        merged.Add(s.Dictionary);
                    }
                    else if (s.Dictionary.IsType(PdfName.ObjStm)) objectStreams.Add((int)number.Integer);
                }
                else if (parsed?.Value is PdfDictionary d && d.IsType(PdfName.Catalog) && lastTrailer?.GetRaw(PdfName.Root) is null)
                {
                    var lone = new PdfDictionary();
                    lone.Set(PdfName.Root, new PdfReference(parsed.Value.Id));
                    lastTrailer = Merge(lastTrailer, lone);
                    merged.Add(lone);
                }
            }
            catch (PdfException)
            {
                // Damaged object: it stays in the table; reading it then yields null
            }
        }

        // 2. All "trailer" dictionaries; the last one with /Root wins
        for (var at = source.IndexOf(Trailer, 0); at >= 0; at = source.IndexOf(Trailer, at + Trailer.Length))
        {
            var lexer = new PdfLexer(source, at + Trailer.Length);
            try
            {
                if (new PdfParser(lexer).ParseObject() is PdfDictionary d && d.GetRaw(PdfName.Root) is not null)
                {
                    lastTrailer = Merge(lastTrailer, d);
                    merged.Add(d);
                }
            }
            catch (PdfException)
            {
            }
        }

        // the /Root that wins must name a catalogue: in a damaged later update it can name an object of an older revision under the same
        // number (issue9418: the information dictionary) — then the newest trailer whose /Root is a catalogue wins, with its own /Info,
        // /Encrypt and /ID, as reference readers choose; before, the copy got the information as its catalogue and lost the outline (No. 813)
        if (lastTrailer is not null && !NamesCatalogue(source, table, lastTrailer.GetRaw(PdfName.Root)))
            for (var i = merged.Count - 2; i >= 0; i--)
            {
                if (merged[i].GetRaw(PdfName.Root) is not { } root || !NamesCatalogue(source, table, root)) continue;
                // up to the chosen one: older trailers fill what it leaves out, nothing comes from the newer revision that is not read
                lastTrailer = null;
                for (var k = 0; k <= i; k++) lastTrailer = Merge(lastTrailer, merged[k]);
                break;
            }

        table.Trailer = lastTrailer ?? new PdfDictionary();
        table.Trailer.Remove(PdfName.Prev);
        table.Trailer.Remove(PdfName.XRefStm);

        // 3. The document registers objects in object streams as soon as it can decrypt
        table.ObjectStreams.AddRange(objectStreams);
        return table;
    }

    /// <summary>Newer values of /Root, /Info, /Encrypt, /ID and /Size override older ones (the file is read from the start).</summary>
    private static PdfDictionary Merge(PdfDictionary? older, PdfDictionary newer)
    {
        var result = new PdfDictionary();
        if (older is not null)
            foreach (var (k, v) in older.Entries) result.Set(k, v);
        foreach (var key in new[] { PdfName.Root, PdfName.Info, PdfName.Encrypt, PdfName.ID, PdfName.Size })
            if (newer.GetRaw(key) is { } value) result.Set(key, value);
        return result;
    }

    /// <summary>
    /// /Root names a catalogue: a dictionary with /Type /Catalog or /Pages. An object that is not directly in the file (in an object stream,
    /// registered only later) cannot be looked at here and counts as one; a damaged object, a stream or anything else does not.
    /// </summary>
    private static bool NamesCatalogue(PdfSource source, XRefTable table, PdfObject? root)
    {
        if (root is PdfDictionary direct) return IsCatalogue(direct);
        if (root is not PdfReference r) return false;
        if (!table.Entries.TryGetValue(r.Id.Number, out var entry) || entry.Type != XRefEntryType.InFile) return true;
        try
        {
            return new PdfParser(new PdfLexer(source)).ParseIndirectObject(entry.Offset)?.Value is PdfDictionary d && IsCatalogue(d);
        }
        catch (PdfException)
        {
            return false;
        }

        static bool IsCatalogue(PdfDictionary d) => d.IsType(PdfName.Catalog) || d.ContainsKey(PdfName.Pages);
    }

    private static bool IsDigit(int c) => c is >= '0' and <= '9';

    /// <summary>Goes back from "obj" over "n g " and returns the start of n.</summary>
    private static long? FindObjectStart(PdfSource source, long objAt)
    {
        var p = objAt - 1;
        while (p >= 0 && PdfLexer.IsWhitespace(source.At(p))) p--;
        var genEnd = p;
        while (p >= 0 && IsDigit(source.At(p))) p--;
        if (p == genEnd || p < 0 || !PdfLexer.IsWhitespace(source.At(p))) return null;
        while (p >= 0 && PdfLexer.IsWhitespace(source.At(p))) p--;
        var numEnd = p;
        while (p >= 0 && IsDigit(source.At(p))) p--;
        if (p == numEnd) return null;
        if (p >= 0 && PdfLexer.IsRegular(source.At(p))) return null;
        return p + 1;
    }
}

/// <summary>Object streams (7.5.7): /N objects, with a header of "number offset" pairs; the objects start at /First.</summary>
public static class ObjectStreamReader
{
    /// <summary>Reads the header: number and position (relative to /First) of each object.</summary>
    /// <param name="data">The decoded stream data.</param>
    /// <param name="dict">The stream dictionary.</param>
    /// <returns>The pairs in header order; reading stops at the first pair that is not two integers.</returns>
    /// <remarks>/N is clamped to 10,000,000 against crafted files.</remarks>
    public static List<(int Number, long Offset)> Header(byte[] data, PdfDictionary dict)
    {
        var n = (int)Math.Clamp(dict.GetInteger(PdfName.N) ?? 0, 0, 10_000_000);
        var lexer = new PdfLexer(PdfSource.FromBytes(data));
        var result = new List<(int, long)>(Math.Min(n, 100_000));
        for (var i = 0; i < n; i++)
        {
            var number = lexer.Next();
            var offset = lexer.Next();
            if (number.Kind != PdfTokenKind.Integer || offset.Kind != PdfTokenKind.Integer) break;
            if (number.Integer is < 0 or > int.MaxValue) continue;
            result.Add(((int)number.Integer, offset.Integer));
        }
        return result;
    }
}
