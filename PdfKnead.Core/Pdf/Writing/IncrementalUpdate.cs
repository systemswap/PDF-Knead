using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Appends an incremental update to a file (ISO 32000-2:2020, section 7.5.6): the original bytes stay exactly as they are, and
/// behind them come the changed and new objects and a cross-reference section whose /Prev points to the previous one. A digital
/// signature covers the bytes of the file as it was signed, so this is the only way to change a signed document without
/// invalidating its signatures.
/// </summary>
/// <remarks>
/// Objects keep their numbers: a replaced object is written under its old number and generation, references in the objects are
/// written as they are (to existing objects of the file or to objects added here). Streams must carry their data in memory. The
/// cross-reference section has the same form as the newest one of the file (table or stream). Encrypted files are not supported
/// yet; <see cref="CanAppend"/> says whether a file qualifies.
/// </remarks>
public sealed class IncrementalUpdate
{
    private const int CopyChunk = 1 << 20;

    private readonly PdfDocument _doc;
    private readonly SortedDictionary<int, (int Generation, PdfObject Value)> _objects = [];
    private readonly Dictionary<int, long> _offsets = [];
    private int _next;

    /// <summary>Starts an update of a document.</summary>
    /// <param name="doc">The opened document; its file bytes are copied unchanged when writing.</param>
    /// <exception cref="NotAppendableException">The file does not qualify (see <see cref="CanAppend"/>).</exception>
    public IncrementalUpdate(PdfDocument doc)
    {
        if (!CanAppend(doc)) throw EncryptionRefusal(doc) is { } refusal ? new NotAppendableException(refusal) : NotAppendable();
        _doc = doc;
        // New numbers after the highest one the file uses: some files state a /Size below their own object numbers, and a new
        // object under such a number would hide an existing one
        var size = doc.Trailer.GetInteger(PdfName.Get("Size")) ?? 0;
        _next = (int)Math.Clamp(Math.Max(size, doc.HighestObjectNumber + 1L), 1, PdfLimits.MaxObjectNumber);
    }

    /// <summary>
    /// Whether an update can be appended: the cross-references were read as stated (not repaired), the header is at the start of the
    /// file, and an encryption, if any, is one an update can be written with (<see cref="EncryptionRefusal"/>).
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <returns><see langword="true"/> if the file qualifies.</returns>
    public static bool CanAppend(PdfDocument doc) =>
        doc.NewestXRefSection is not null && doc.Trailer.GetRaw(PdfName.Root) is PdfReference && EncryptionRefusal(doc) is null;

    /// <summary>
    /// Why an encrypted file takes no update, or null if it does (or is not encrypted): the update is encrypted with the file's own key
    /// (ISO 32000-2, 7.6; docs/FEHLERGEDAECHTNIS.md, No. 543) — not with RC4, which PAdES no longer recommends (EN 319 142-1, 5.5), and not
    /// where ISO/TS 32004 asks for a MAC per revision (bit 13 of /P cleared from revision 6, /KDFSalt, /AuthCode), which PDF Knead cannot
    /// make yet.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <returns>The reason (translated), or null.</returns>
    public static string? EncryptionRefusal(PdfDocument doc) => EncryptionRefusal(doc.Security, doc.EncryptDictionary, doc.Trailer);

    /// <inheritdoc cref="EncryptionRefusal(PdfDocument)"/>
    /// <param name="security">The file's security handler; null if it is not encrypted.</param>
    /// <param name="encrypt">Its /Encrypt dictionary.</param>
    /// <param name="trailer">Its trailer.</param>
    internal static string? EncryptionRefusal(Security.StandardSecurityHandler? security, PdfDictionary? encrypt, PdfDictionary trailer)
    {
        if (security is null) return null;
        if (security.StringCipher == Security.PdfCipher.Rc4 || security.StreamCipher == Security.PdfCipher.Rc4)
            return Tr.T("Mit RC4 verschlüsselte Dateien lassen sich nicht signieren. Bitte zuerst mit neuem Schutz (AES) speichern.");
        // bit 13 as sealed in /Perms where valid, else as in /P (No. 544): a /P edited afterwards does not hide the MAC requirement
        var macRequired = security.Revision >= 6 && (security.RawPermissions & (1 << 12)) == 0
                          || encrypt?.ContainsKey(PdfName.Get("KDFSalt")) == true || trailer.ContainsKey(PdfName.Get("AuthCode"));
        return macRequired ? Tr.T("Diese Datei ist zusätzlich gegen Veränderung geschützt (ISO/TS 32004); PDF-Knead kann sie noch nicht signieren.") : null;
    }

    /// <summary>Adds a new object under the next free number.</summary>
    /// <param name="value">The object; a stream must carry its data in memory.</param>
    /// <returns>A reference to the new object, to be used in other objects of the update.</returns>
    /// <exception cref="PdfLimitException">No object number is left.</exception>
    public PdfReference Add(PdfObject value)
    {
        if (_next >= PdfLimits.MaxObjectNumber) throw new PdfLimitException(Tr.T("Zu viele Objekte."));
        var number = _next++;
        _objects[number] = (0, Checked(value));
        return new PdfReference(new PdfObjectId(number, 0));
    }

    /// <summary>Replaces an object of the file; it keeps its number and generation.</summary>
    /// <param name="id">The object of the file.</param>
    /// <param name="value">The new content; a stream must carry its data in memory.</param>
    public void Replace(PdfObjectId id, PdfObject value) => _objects[id.Number] = (id.Generation, Checked(value));

    /// <summary>Number of objects in the update.</summary>
    public int Count => _objects.Count;

    /// <summary>After <see cref="WriteTo"/>: position of each written object in the output (also of the cross-reference stream).</summary>
    public IReadOnlyDictionary<int, long> Offsets => _offsets;

    /// <summary>Writes the original file followed by the update.</summary>
    /// <param name="output">The output; it must start empty (offsets are counted from its start).</param>
    /// <returns>The length of the written file.</returns>
    /// <exception cref="NotAppendableException">The document had to repair its cross-references while the update was built (an object
    /// listed at a wrong place is read only when needed); nothing is written, because the update would continue the wrong table.</exception>
    public long WriteTo(Stream output)
    {
        var trailer = Trailer();
        for (long at = 0; at < _doc.FileSize; at += CopyChunk)
            output.Write(_doc.FileBytes(at, CopyChunk));
        return Write(output, trailer);
    }

    /// <summary>
    /// Writes only the update, the part that follows the original bytes: for callers that already hold those bytes (a file being saved
    /// in several appending steps) and need not copy them again.
    /// </summary>
    /// <param name="output">The output; positions in the update are counted as if the original bytes came first.</param>
    /// <returns>The length of the whole file (original bytes and update).</returns>
    /// <exception cref="NotAppendableException">As for <see cref="WriteTo"/>.</exception>
    public long WriteUpdateTo(Stream output) => Write(output, Trailer());

    private long Write(Stream output, PdfDictionary trailer)
    {
        _offsets.Clear();
        var counter = new CountingStream(output, _doc.FileSize);
        var last = _doc.FileSize > 0 ? _doc.FileBytes(_doc.FileSize - 1, 1)[0] : (byte)'\n';
        if (last is not ((byte)'\n' or (byte)'\r')) counter.Write("\n"u8);

        var documentXmp = DocumentXmpNumber();
        foreach (var (number, (generation, value)) in _objects)
        {
            _offsets[number] = counter.Position;
            WriteObject(counter, number, generation, value, _doc.Security, number == documentXmp);
        }

        long section;
        if (_doc.NewestXRefIsStream)
        {
            var number = _next++;
            section = counter.Position;
            _offsets[number] = section;
            trailer.Set(PdfName.Get("Size"), PdfInteger.Of(_next));
            WriteXRefStream(counter, number, trailer);
        }
        else
        {
            section = counter.Position;
            trailer.Set(PdfName.Get("Size"), PdfInteger.Of(_next));
            WriteXRefTable(counter, trailer);
        }
        counter.Write(Encoding.ASCII.GetBytes($"startxref\n{section.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n"));
        return counter.Position;
    }

    private static NotAppendableException NotAppendable() => new(Tr.T("An diese Datei lässt sich nichts anhängen."));

    private static PdfObject Checked(PdfObject value)
    {
        if (value is PdfStream { RawData: null }) throw new ArgumentException(Tr.T("Ein angehängter Datenstrom muss seine Daten im Speicher haben."), nameof(value));
        return value;
    }

    /// <summary>
    /// What a trailer of the update does not carry on from the newest one: the standard leaves out only /Prev — the other exceptions belong
    /// to the previous section: its cross-reference stream (/Type, /W, /Index, the stream keys, /XRefStm of a hybrid file), its size, its
    /// MAC (ISO/TS 32004, 6.5: valid only for its revision), the outdated checksum (it would be wrong) and /ID (formed anew below).
    /// </summary>
    private static readonly HashSet<string> NotCarried =
    [
        "Prev", "XRefStm", "Size", "Type", "W", "Index", "Length", "Filter", "DecodeParms", "F", "FFilter", "FDecodeParms", "DL",
        "DocChecksum", "AuthCode", "ID",
    ];

    /// <summary>
    /// Trailer of the update: every entry of the newest trailer as read — keys of other programs and a direct /Info included — except
    /// <see cref="NotCarried"/>; /Root and /Info, where a later trailer leaves them out, from the older ones; /Encrypt and the first part of /ID
    /// as before, a new second part, /Prev to the last section (ISO 32000-2, 7.5.6; /Encrypt the same in every trailer, 7.6.1;
    /// docs/FEHLERGEDAECHTNIS.md, No. 548). /Size follows when writing, never below the previous one.
    /// </summary>
    private PdfDictionary Trailer()
    {
        var old = _doc.Trailer;
        var trailer = new PdfDictionary();
        if (_doc.NewestTrailer is { } newest)
            foreach (var (key, value) in newest.Entries)
                if (!NotCarried.Contains(key.Value)) trailer.Set(key, value);
        if (!trailer.ContainsKey(PdfName.Root)) trailer.Set(PdfName.Root, old.GetRaw(PdfName.Root)!);
        if (!trailer.ContainsKey(PdfName.Get("Info")) && old.GetRaw(PdfName.Get("Info")) is PdfReference info) trailer.Set(PdfName.Get("Info"), info);
        trailer.Remove(PdfName.Encrypt);
        if (_doc.Security is not null && old.GetRaw(PdfName.Encrypt) is { } encrypt and not PdfNull) trailer.Set(PdfName.Encrypt, encrypt);
        var changing = RandomNumberGenerator.GetBytes(16);
        // an encrypted file keeps the identifier its key was derived with — also an empty one, where it had none (docs/FEHLERGEDAECHTNIS.md,
        // No. 345)
        // a shorter one is no identifier in the sense of the standard (ISO 32000-2, Table 15: at least 16 bytes; No. 746)
        var permanent = _doc.Security is { } security ? security.FileId
            : old.GetArray(PdfName.Get("ID")) is { Count: 2 } id && id.Get(0) is PdfString { Bytes.Length: >= 16 } first ? first.Bytes : changing;
        trailer.Set(PdfName.Get("ID"), new PdfArray { new PdfString(permanent, isHex: true), new PdfString(changing, isHex: true) });
        trailer.Set(PdfName.Prev, PdfInteger.Of(_doc.NewestXRefSection ?? throw NotAppendable()));
        return trailer;
    }

    /// <summary>
    /// Writes one object; in an encrypted file its strings and its stream data with the file's key and the object's number and generation —
    /// the plain /Contents of a signature dictionary left out (7.6.2), a metadata stream left plain where the file says so (/EncryptMetadata
    /// false), and a stream that names its own crypt filter written as it is (docs/FEHLERGEDAECHTNIS.md, No. 543).
    /// </summary>
    /// <summary>The number of the document's metadata stream after the update (its catalogue replaced or not); null without.</summary>
    private int? DocumentXmpNumber()
    {
        var root = (_doc.Trailer.GetRaw(PdfName.Root) as PdfReference)?.Id.Number;
        var catalog = root is { } r && _objects.TryGetValue(r, out var replaced) ? replaced.Value as PdfDictionary : _doc.Catalog;
        return (catalog?.GetRaw(PdfName.Metadata) as PdfReference)?.Id.Number;
    }

    /// <param name="documentXmp">The object is the document's metadata stream: plain where the file says so (/EncryptMetadata false; that of
    /// pages and images is encrypted all the same, docs/FEHLERGEDAECHTNIS.md, No. 551).</param>
    private static void WriteObject(CountingStream output, int number, int generation, PdfObject value, Security.StandardSecurityHandler? security,
        bool documentXmp)
    {
        output.Write(Encoding.ASCII.GetBytes($"{number} {generation} obj\n"));
        var id = new PdfObjectId(number, generation);
        Func<byte[], byte[]>? encryptString = security is null ? null : bytes => security.EncryptString(id, bytes);
        if (value is PdfStream stream)
        {
            var data = stream.RawData!;
            if (security is not null && !(documentXmp && stream.Dictionary.IsType(PdfName.Get("Metadata")) && !security.EncryptMetadata)
                && !(Filters.StreamFilters.Chain(stream.Dictionary) is [("Crypt", _), ..]))
                data = security.EncryptStream(id, data);
            var dictionary = new PdfDictionary(stream.Dictionary.Count + 1);
            foreach (var (key, entry) in stream.Dictionary.Entries) dictionary.Set(key, entry);
            dictionary.Set(PdfName.Get("Length"), PdfInteger.Of(data.Length));
            // the data comes from memory, the decoded length as a hint is not vouched for (ISO 32000-2, 7.3.8.2; docs/FEHLERGEDAECHTNIS.md, No. 726)
            dictionary.Remove(PdfName.Get("DL"));
            PdfSerializer.Write(dictionary, output, encryptString);
            output.Write("\nstream\n"u8);
            output.Write(data);
            output.Write("\nendstream"u8);
        }
        else
        {
            PdfSerializer.Write(value, output, encryptString);
        }
        output.Write("\nendobj\n"u8);
    }

    /// <summary>Classic table: one subsection per run of consecutive numbers, 20 bytes per entry (ISO 32000-2, 7.5.4).</summary>
    private void WriteXRefTable(CountingStream output, PdfDictionary trailer)
    {
        var text = new StringBuilder("xref\n");
        foreach (var run in Runs(_offsets.Keys))
        {
            text.Append(run.First).Append(' ').Append(run.Count).Append('\n');
            for (var number = run.First; number < run.First + run.Count; number++)
                text.Append(_offsets[number].ToString("D10", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(_objects[number].Generation.ToString("D5", CultureInfo.InvariantCulture)).Append(" n\r\n");
        }
        text.Append("trailer\n");
        output.Write(Encoding.ASCII.GetBytes(text.ToString()));
        PdfSerializer.Write(trailer, output);
        output.Write("\n"u8);
    }

    /// <summary>Cross-reference stream, uncompressed, fields 1/8/2 bytes wide (ISO 32000-2, 7.5.8).</summary>
    private void WriteXRefStream(CountingStream output, int number, PdfDictionary trailer)
    {
        var numbers = _offsets.Keys.OrderBy(n => n).ToList();
        var index = new PdfArray();
        foreach (var run in Runs(numbers))
        {
            index.Add(PdfInteger.Of(run.First));
            index.Add(PdfInteger.Of(run.Count));
        }
        var data = new byte[numbers.Count * 11];
        for (var i = 0; i < numbers.Count; i++)
        {
            var row = data.AsSpan(i * 11, 11);
            row[0] = 1;
            BinaryPrimitives.WriteInt64BigEndian(row[1..9], _offsets[numbers[i]]);
            var generation = numbers[i] == number ? 0 : _objects[numbers[i]].Generation;
            BinaryPrimitives.WriteUInt16BigEndian(row[9..11], (ushort)generation);
        }
        trailer.Set(PdfName.Type, PdfName.Get("XRef"));
        trailer.Set(PdfName.Get("W"), new PdfArray { PdfInteger.Of(1), PdfInteger.Of(8), PdfInteger.Of(2) });
        trailer.Set(PdfName.Get("Index"), index);
        trailer.Set(PdfName.Get("Length"), PdfInteger.Of(data.Length));
        output.Write(Encoding.ASCII.GetBytes($"{number} 0 obj\n"));
        PdfSerializer.Write(trailer, output);
        output.Write("\nstream\n"u8);
        output.Write(data);
        output.Write("\nendstream\nendobj\n"u8);
    }

    private static IEnumerable<(int First, int Count)> Runs(IEnumerable<int> numbers)
    {
        int? first = null;
        var count = 0;
        foreach (var number in numbers.OrderBy(n => n))
        {
            if (first is { } start && number == start + count)
            {
                count++;
                continue;
            }
            if (first is { } done) yield return (done, count);
            first = number;
            count = 1;
        }
        if (first is { } last) yield return (last, count);
    }

    /// <summary>Counts the written bytes, so that offsets need no seekable output.</summary>
    private sealed class CountingStream(Stream inner, long start) : Stream
    {
        private long _written = start;

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            _written += buffer.Length;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _written;

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
