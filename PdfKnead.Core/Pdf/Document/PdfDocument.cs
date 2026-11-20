using System.Collections.Concurrent;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.IO;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Parsing;
using PdfKnead.Core.Pdf.Security;

namespace PdfKnead.Core.Pdf.Document;

/// <summary>
/// An open PDF file: cross-references, decryption, objects, pages and metadata.
/// Objects are read only when they are needed and are then cached.
/// </summary>
/// <remarks>Reading from several threads at the same time is allowed (the renderer relies on it).
/// <see cref="Dispose"/> releases the underlying source; spans and objects backed by it are invalid afterwards.</remarks>
public sealed class PdfDocument : IPdfResolver, IDisposable
{
    private readonly PdfSource _source;
    private readonly ConcurrentDictionary<int, PdfObject> _cache = new();
    private readonly ConcurrentDictionary<int, Lazy<ObjectStream?>> _objectStreams = new();
    // decoded object streams are kept for the objects read later — within a budget; the least recently used go first and are decoded again
    // when needed. Without it a crafted file of 2.7 MB kept 6.9 GB (several object streams, each a decompression bomb just under the
    // limit; docs/FEHLERGEDAECHTNIS.md, No. 745)
    private long _objectStreamBytes, _objectStreamsDecoded, _objectStreamClock;
    private readonly Lock _objectStreamEviction = new();
    private volatile bool _objectStreamsIndexed;
    private bool _indexingObjectStreams;   // only under _repairLock

    /// <summary>
    /// Things built from objects and shared by all pages (fonts …), keyed by object identity.
    /// Several pages are built at the same time, hence a concurrent dictionary.
    /// </summary>
    internal ConcurrentDictionary<PdfObject, object> Built { get; } = new(ReferenceEqualityComparer.Instance);

    // Functions and colour spaces decode streams (samples, ICC profiles): only once per object
    private readonly ConcurrentDictionary<PdfObject, object> _functions = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<PdfObject, object> _colorSpaces = new(ReferenceEqualityComparer.Instance);
    private static readonly object NoResult = new();

    internal PdfFunction? CachedFunction(PdfObject key, Func<PdfFunction?> build) =>
        (_functions.TryGetValue(key, out var f) ? f : _functions.GetOrAdd(key, _ => build() ?? NoResult)) as PdfFunction;

    // samples of sampled functions (Type 0) kept with the functions: within a budget — a crafted file of 334 KB with twenty functions of
    // 16 million samples each took 3.5 GB (docs/FEHLERGEDAECHTNIS.md, No. 756)
    private long _functionSamples;

    /// <summary>Most samples of sampled functions the document keeps (8 bytes each): <see cref="PdfLimits.Budget"/> with 256 MB at most and
    /// 32 MB at least; real files need a few megabytes. Tests set a smaller value.</summary>
    internal long FunctionSampleBudget { get; set; } = PdfLimits.Budget(256L << 20, 32L << 20) / 8;

    // colour tables of ICC profiles kept with the colour spaces: within a budget — a crafted file of 626 KB with twenty profiles took 2.9 GB
    // (docs/FEHLERGEDAECHTNIS.md, No. 759)
    private long _iccEntries;

    /// <summary>Most entries of ICC colour tables the document keeps (4 bytes each): <see cref="PdfLimits.Budget"/> with 512 MB at most and
    /// 64 MB at least; real files need a few megabytes per profile. Tests set a smaller value.</summary>
    internal long IccTableBudget { get; set; } = PdfLimits.Budget(512L << 20, 64L << 20) / 4;

    /// <summary>Reserves room for the colour tables of a profile; false if the budget would be exceeded (its alternate space is used then).</summary>
    internal bool ReserveIccTables(long entries)
    {
        if (Interlocked.Add(ref _iccEntries, entries) <= IccTableBudget) return true;
        Interlocked.Add(ref _iccEntries, -entries);
        return false;
    }

    /// <summary>Reserves room for the samples of a sampled function; false if the budget would be exceeded (the function is then not used).</summary>
    internal bool ReserveFunctionSamples(long count)
    {
        if (Interlocked.Add(ref _functionSamples, count) <= FunctionSampleBudget) return true;
        Interlocked.Add(ref _functionSamples, -count);
        return false;
    }

    internal ColorSpace CachedColorSpace(PdfObject key, Func<ColorSpace> build) =>
        // while DeviceCMYK belongs to a page with its own output intent, a space built now may contain it: not shared with other pages
        ColorSpace.PageScoped ? build() : (ColorSpace)(_colorSpaces.TryGetValue(key, out var s) ? s : _colorSpaces.GetOrAdd(key, _ => build()));

    /// <summary>DeviceCMYK per page with its own /OutputIntents; see <see cref="ColorSpace.PageCmyk"/>.</summary>
    internal System.Runtime.CompilerServices.ConditionalWeakTable<PdfDictionary, ColorSpace.StrongBox> PageCmykSpaces { get; } = new();

    /// <summary>DeviceCMYK of this document (output intent profile or FOGRA39), determined once; see <see cref="ColorSpace.DeviceCmykOf"/>.</summary>
    internal ColorSpace? DeviceCmykSpace { get; set; }
    private readonly List<string> _problems = [];
    private readonly Lock _repairLock = new();
    private readonly long _headerOffset;
    private XRefTable _xref;
    // After a rebuild: contents of the object streams. A separate, thread-safe table: adding the entries to the already
    // published table while other threads read it (a Dictionary without a lock) is not safe.
    private ConcurrentDictionary<int, XRefEntry>? _indexed;   // only created when needed (almost never)
    private int? _encryptNumber;
    private List<PdfPage>? _pages;

    [ThreadStatic] private static HashSet<int>? _resolving;

    private PdfDocument(PdfSource source, string? password, string? filePath, StandardSecurityHandler? known = null)
    {
        _source = source;
        _knownSecurity = known;
        FilePath = filePath;
        try
        {
            (HeaderVersion, _headerOffset) = FindHeader(source);
            if (_headerOffset > 0) AddProblem(Tr.T("Vor dem PDF-Kopf stehen {0} fremde Bytes.", _headerOffset));

            try
            {
                _xref = XRefReader.Read(source, _headerOffset);
            }
            catch (PdfException ex)
            {
                _xref = Rebuild(Tr.T("Querverweise defekt ({0}) — aus der Datei rekonstruiert.", ex.Message));
            }

            AttachResolver(_xref.Trailer);
            SetUpSecurity(password);

            // Check the catalogue; if it is missing, the table is wrong, so rebuild it once
            if (Catalog is null && !_xref.Rebuilt)
            {
                _xref = Rebuild(Tr.T("Katalog nicht gefunden — Querverweise aus der Datei rekonstruiert."));
                AttachResolver(_xref.Trailer);
                SetUpSecurity(password);
            }
            if (Catalog is null) throw new PdfException(Tr.T("Die Datei enthält keinen lesbaren Dokumentkatalog."));
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>Opens a file.</summary>
    /// <param name="path">Path of the file.</param>
    /// <param name="password">The open password for an encrypted file; null if there is none.</param>
    /// <returns>The open document; the caller disposes it.</returns>
    /// <exception cref="PdfPasswordException">A password is required or the given one is wrong.</exception>
    /// <exception cref="PdfException">The file is not a PDF or cannot be repaired.</exception>
    public static PdfDocument Open(string path, string? password = null) => new(PdfSource.FromFile(path), password, path);

    /// <summary>Opens a PDF held in memory.</summary>
    /// <param name="data">The file contents; the document keeps using this array.</param>
    /// <param name="password">The open password for an encrypted file; null if there is none.</param>
    /// <returns>The open document; the caller disposes it.</returns>
    /// <exception cref="PdfPasswordException">A password is required or the given one is wrong.</exception>
    /// <exception cref="PdfException">The data is not a PDF or cannot be repaired.</exception>
    public static PdfDocument Open(byte[] data, string? password = null) => new(PdfSource.FromBytes(data), password, null);

    /// <summary>Opens a file just written with the key it was written with (no password needed), for checking it (No. 461).</summary>
    /// <param name="data">The written file.</param>
    /// <param name="security">The handler the writer encrypted with; null if not encrypted.</param>
    /// <returns>The document.</returns>
    internal static PdfDocument OpenWritten(byte[] data, StandardSecurityHandler? security) => new(PdfSource.FromBytes(data), null, null, security);

    /// <inheritdoc cref="OpenWritten(byte[], StandardSecurityHandler?)"/>
    /// <param name="path">The written file.</param>
    internal static PdfDocument OpenWritten(string path, StandardSecurityHandler? security) => new(PdfSource.FromFile(path), null, path, security);

    /// <inheritdoc cref="OpenWritten(byte[], StandardSecurityHandler?)"/>
    /// <param name="source">The written file, already open; the document takes ownership of it.</param>
    internal static PdfDocument OpenWritten(PdfSource source, StandardSecurityHandler? security) => new(source, null, null, security);

    /// <summary>Opens a source that is already open (such as <see cref="IO.MemoryPdf"/>); the document takes ownership of it.</summary>
    internal static PdfDocument Open(PdfSource source, string? password = null) => new(source, password, null);

    /// <summary>
    /// Opens an earlier revision of this file — its bytes up to the end of that revision (ISO 32000-2:2020, 7.5.6) — without copying
    /// them; valid while this document is open.
    /// </summary>
    /// <param name="length">End of the revision in bytes.</param>
    /// <param name="password">Password of an encrypted file; null if none.</param>
    /// <returns>The revision; the caller disposes it before this document.</returns>
    public PdfDocument OpenRevision(long length, string? password = null) => new(_source.Prefix(length), password, null);

    /// <summary>Opens the program's own output (trial run) with the key it was encrypted with, without a password.</summary>
    internal static PdfDocument Open(PdfSource source, StandardSecurityHandler security) => new(source, null, null, security);

    /// <summary>Predefined key (<see cref="Open(PdfSource, StandardSecurityHandler)"/>) used instead of a password.</summary>
    private readonly StandardSecurityHandler? _knownSecurity;

    /// <summary>Path of the opened file; null if the document was opened from memory.</summary>
    public string? FilePath { get; }

    /// <summary>Size of the source in bytes.</summary>
    public long FileSize => _source.Length;

    /// <summary>
    /// Returns a section of the original file bytes without copying, valid while the document is open. Digital signatures cover
    /// byte ranges of the file exactly as it was written, so checking and extending them needs these bytes, not the parsed objects.
    /// </summary>
    /// <param name="offset">Start position.</param>
    /// <param name="length">Requested length; shortened at the end of the file.</param>
    /// <returns>The section; empty if <paramref name="offset"/> is outside the file.</returns>
    /// <exception cref="ObjectDisposedException">The document has been disposed.</exception>
    public ReadOnlySpan<byte> FileBytes(long offset, long length) => _source.Span(offset, length);

    /// <summary>
    /// Whether the file the document reads from is still as it was when opened (see <see cref="PdfSource.IsUnchangedOnDisk"/>): false if
    /// another program wrote into it in place, null if the document is completely in memory (small files, copies).
    /// </summary>
    /// <returns>True if unchanged, false if changed in place, null without an open file.</returns>
    public bool? IsUnchangedOnDisk() => _source.IsUnchangedOnDisk();

    /// <summary>
    /// Offset of the newest cross-reference section as stated after „startxref“, for appending an incremental update; null if the
    /// file was repaired or has bytes before its header (its offsets cannot be trusted for an update).
    /// </summary>
    public long? NewestXRefSection => _xref.Rebuilt || _headerOffset != 0 || _xref.NewestSection < 0 ? null : _xref.NewestSection;

    /// <summary>Whether the cross-references had to be rebuilt from the file (they were missing, damaged or pointed to wrong places).</summary>
    internal bool Repaired => _xref.Rebuilt;

    /// <summary>Whether the newest cross-reference section is a stream (an appended section then is one as well).</summary>
    public bool NewestXRefIsStream => _xref.NewestIsStream;

    /// <summary>
    /// The objects stored directly in the file (not in object streams) with the byte position of their „n g obj“, as the cross-references say
    /// (bytes before the header added) — for checks of the file's syntax (PDF/A).
    /// </summary>
    internal IEnumerable<(int Number, long Position)> StoredObjects =>
        _xref.Entries.Where(e => e.Value.Type == XRefEntryType.InFile).Select(e => (e.Key, e.Value.Offset + _headerOffset));

    /// <summary>Version from the header line ("1.7").</summary>
    public string HeaderVersion { get; }

    /// <summary>Effective version: the higher of the header version and /Version in the catalogue (for incremental updates).</summary>
    public string Version =>
        Catalog?.GetName(PdfName.Version)?.Value is { } v && CompareVersions(v, HeaderVersion) > 0 ? v : HeaderVersion;

    /// <summary>The trailer dictionary.</summary>
    public PdfDictionary Trailer => _xref.Trailer;

    /// <summary>The trailer of the newest section as read (not merged with older ones); <see langword="null"/> if the cross-references were
    /// rebuilt.</summary>
    public PdfDictionary? NewestTrailer => _xref.Rebuilt ? null : _xref.NewestTrailer;

    /// <summary>The document catalogue (/Root). Opening fails if it cannot be read, so it is normally not null.</summary>
    public PdfDictionary? Catalog => Trailer.Get(PdfName.Root).AsDictionary();

    /// <summary>Document information from /Info, parsed on every access.</summary>
    public PdfInfo Info => PdfInfo.From(Trailer.GetDictionary(PdfName.Info));

    /// <summary>The security handler of an encrypted document; null if the document is not encrypted.</summary>
    public StandardSecurityHandler? Security { get; private set; }

    /// <summary>
    /// The integrity protection of an encrypted file (MAC, ISO/TS 32004), checked once: valid, violated, missing although asked for, not
    /// checkable, or none (docs/FEHLERGEDAECHTNIS.md, No. 556).
    /// </summary>
    public MacCheck Integrity => _integrity ??= PdfMac.Verify(this);

    private MacCheck? _integrity;

    /// <summary>
    /// The encryption dictionary that <see cref="Security"/> was set up with. Saving must reuse it rather than read it
    /// from the trailer again: a damaged reference there can point to a different object and produce an unreadable copy.
    /// See docs/FEHLERGEDAECHTNIS.md, No. 42.
    /// </summary>
    public PdfDictionary? EncryptDictionary { get; private set; }

    /// <summary>True if the document is encrypted.</summary>
    public bool IsEncrypted => Security is not null;

    /// <summary>True if the cross-references were rebuilt from the file contents because the file is damaged.</summary>
    public bool WasRepaired => _xref.Rebuilt;

    private int _missingCjkFonts;

    /// <summary>
    /// True if a Chinese, Japanese or Korean font is needed (not embedded) but the "Schriften-CJK" folder is missing, so the
    /// text cannot be displayed. Set while drawing, also on background threads.
    /// </summary>
    public bool MissingCjkFonts => Volatile.Read(ref _missingCjkFonts) != 0;

    /// <summary>
    /// Raised once per document as soon as <see cref="MissingCjkFonts"/> first becomes true (may be raised on a background thread).
    /// Static because pages from several files (insert, merge) bring their own documents.
    /// </summary>
    public static event Action<PdfDocument>? MissingCjkFontsFound;

    internal void ReportMissingCjkFonts()
    {
        if (Interlocked.Exchange(ref _missingCjkFonts, 1) == 0) MissingCjkFontsFound?.Invoke(this);
    }

    /// <summary>Problems noticed while reading (localised, for display); a snapshot copy, without duplicates.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            lock (_problems) return [.. _problems];
        }
    }

    /// <summary>
    /// Object numbers whose cross-reference entry differs from <paramref name="earlier"/>, an earlier revision of the same file (its
    /// bytes up to the end of that revision, see ISO 32000-2:2020, section 7.5.6): new, replaced and freed objects, and the objects of
    /// a replaced object stream, whose own entries look unchanged.
    /// </summary>
    /// <param name="earlier">The earlier revision.</param>
    /// <returns>The numbers in ascending order, or null if one of the two tables had to be rebuilt (then nothing can be said reliably).</returns>
    public IReadOnlyList<int>? ObjectsChangedSince(PdfDocument earlier)
    {
        if (_xref.Rebuilt || earlier._xref.Rebuilt) return null;
        var changed = new HashSet<int>();
        foreach (var (number, entry) in _xref.Entries)
            if (earlier._xref.Entries.TryGetValue(number, out var old) ? old != entry : entry.Type != XRefEntryType.Free) changed.Add(number);
        foreach (var (number, old) in earlier._xref.Entries)
            if (old.Type != XRefEntryType.Free && !_xref.Entries.ContainsKey(number)) changed.Add(number);
        foreach (var (number, entry) in _xref.Entries)
            if (entry.Type == XRefEntryType.Compressed && changed.Contains((int)entry.Offset)) changed.Add(number);
        return [.. changed.Order()];
    }

    /// <summary>The highest object number the cross-references know (free entries included); 0 if there is none.</summary>
    public int HighestObjectNumber => Math.Max(_xref.Entries.Count == 0 ? 0 : _xref.Entries.Keys.Max(), _xref.HighestFree);

    /// <summary>
    /// The numbers of the objects the cross-references know (not free) and those found in object streams, in ascending order — for walking
    /// all objects without counting up to <see cref="HighestObjectNumber"/>, which a damaged table can put in the billions.
    /// </summary>
    internal IEnumerable<int> ObjectNumbers =>
        _xref.Entries.Where(e => e.Value.Type != XRefEntryType.Free).Select(e => e.Key).Concat(_indexed?.Keys ?? []).Distinct().Order();

    /// <summary>Whether the cross-references know an object with this number (not free).</summary>
    /// <param name="number">The object number.</param>
    /// <returns>true if the object exists according to the table.</returns>
    public bool HasObject(int number) => _xref.Entries.TryGetValue(number, out var entry) && entry.Type != XRefEntryType.Free;

    /// <summary>Number of objects according to the cross-references (free entries not counted).</summary>
    /// <remarks>Counted on every access.</remarks>
    public int ObjectCount => _xref.Entries.Count(e => e.Value.Type != XRefEntryType.Free) + (_indexed?.Count ?? 0);

    /// <summary>The pages in document order, read from the page tree on first access.</summary>
    /// <remarks>If the page tree yields no page, all objects with /Type /Page are collected in object number order.</remarks>
    public IReadOnlyList<PdfPage> Pages => _pages ??= LoadPages();

    /// <summary>Number of pages.</summary>
    public int PageCount => Pages.Count;

    // ------------------------------------------------------------ Header and security

    /// <summary>
    /// Finds the header line. If it is missing but the first bytes contain " obj" and the file contains
    /// "/Catalog", the file is read anyway (as widely used readers do), with version 1.4; otherwise it is not a PDF file.
    /// </summary>
    private (string Version, long Offset) FindHeader(PdfSource source)
    {
        var span = source.Span(0, PdfHeader.SearchLength);
        var at = span.IndexOf("%PDF-"u8);
        if (at >= 0 && PdfHeader.FindVersion(span) is { } version) return (version, at);

        if (span.IndexOf(" obj"u8) is >= 0 && source.IndexOf("/Catalog"u8, 0) >= 0)
        {
            AddProblem(Tr.T("Der PDF-Kopf fehlt — Datei trotzdem gelesen."));
            return ("1.4", 0);
        }
        throw new PdfException(Tr.T("Das ist keine PDF-Datei."));
    }

    /// <summary>
    /// Unlocks attachments that are encrypted with a password of their own (only the attachments are encrypted, the document itself is
    /// open): with the right password they can be read, searched before redacting and carried over on saving — before, saving broke off
    /// and the search before redacting could only report them as not searchable (decided on 4.11.2026;
    /// docs/FEHLERGEDAECHTNIS.md, No. 459). Objects already read stay valid: strings and other streams are not encrypted.
    /// </summary>
    /// Every encrypted attachment must decrypt to a whole padding under the key: a key that matches the password check but not the
    /// attachment (the public test file FRC_3.5_AuthEvent_EFOpen decrypts with no variant of the standard) leaves them locked —
    /// taken, the garbage would have been written anew and the attachment destroyed without a word.
    /// </summary>
    /// <param name="password">The password of the attachments.</param>
    /// <returns>Whether the attachments are open now (also <see cref="AttachmentUnlock.Opened"/> if they were not locked).</returns>
    public AttachmentUnlock UnlockAttachments(string password)
    {
        if (Security is not { AttachmentsLocked: true } locked || EncryptDictionary is not { } encrypt) return AttachmentUnlock.Opened;
        StandardSecurityHandler opened;
        try
        {
            opened = StandardSecurityHandler.Open(encrypt, Trailer.Get(PdfName.ID) as PdfArray, password);
        }
        catch (PdfPasswordException)
        {
            return AttachmentUnlock.WrongPassword;
        }
        if (opened.AttachmentsLocked) return AttachmentUnlock.WrongPassword;
        Security = opened;
        opened.AuthenticationFailed += Tampered;
        foreach (var number in (_embeddedFiles ?? CollectEmbeddedFiles()).Order())
        {
            if (GetObject(number) is not PdfStream stream || stream.RawData is not null || stream.Id is not { } id) continue;
            var cipher = StreamCipherFor(stream);
            if (cipher == PdfCipher.None || opened.DecryptsWhole(id, _source.ReadBytes(stream.DataOffset, stream.DataLength), cipher)) continue;
            Security = locked;
            AddProblem(Tr.T("Ein Dateianhang lässt sich auch mit dem Passwort nicht entschlüsseln — er bleibt gesperrt."));
            return AttachmentUnlock.NotDecryptable;
        }
        return AttachmentUnlock.Opened;
    }

    /// <summary>The result of <see cref="UnlockAttachments"/>.</summary>
    public enum AttachmentUnlock
    {
        /// <summary>The attachments are open.</summary>
        Opened,

        /// <summary>The password does not open them.</summary>
        WrongPassword,

        /// <summary>The password matches, but an attachment does not decrypt with it: they stay locked.</summary>
        NotDecryptable,
    }

    private void SetUpSecurity(string? password)
    {
        Security = null;
        _integrity = null;
        EncryptDictionary = null;
        _encryptNumber = null;
        _cache.Clear();
        ForgetObjectStreams();

        var raw = Trailer.GetRaw(PdfName.Encrypt);
        if (raw is null) return;
        if (raw is PdfReference r) _encryptNumber = r.Id.Number;
        if (Trailer.Get(PdfName.Encrypt) is not PdfDictionary encrypt) return;

        var id = Trailer.Get(PdfName.ID) as PdfArray;
        Security = _knownSecurity ?? StandardSecurityHandler.Open(encrypt, id, password);
        // revision 4 with a short RC4 key whose data only decode with the key padded to 16 bytes (a read tolerance, No. 894)
        if (_knownSecurity is null && Security.PaddedForReading() is { } padded && DecodesOnlyWith(Security, padded))
        {
            Security = padded;
            AddProblem(Tr.T("Die Daten sind mit einem auf 16 Byte aufgefüllten Schlüssel verschlüsselt (nicht nach ISO 32000-2, 7.6.3.3) — so gelesen; eine Kopie wird nach der Norm verschlüsselt."));
        }
        Security.AuthenticationFailed += Tampered;
        EncryptDictionary = encrypt;
        if (Security.AttachmentsLocked) AddProblem(Tr.T("Dateianhänge sind mit einem Passwort geschützt."));
        // the encryption data said, not silently taken (docs/FEHLERGEDAECHTNIS.md, No. 544)
        foreach (var note in Security.Notes) AddProblem(note);
        switch (Security.Perms)
        {
            case PermsState.Missing or PermsState.Invalid when Security.Revision >= 6:
                AddProblem(Tr.T("Die Rechte dieser Datei sind nicht versiegelt (/Perms fehlt oder passt nicht) — sie könnten nachträglich verändert sein."));
                break;
            case PermsState.DiffersFromP:
                AddProblem(Tr.T("Die offenen Rechte (/P) weichen von den versiegelten ab; es gelten die versiegelten. Die Datei verstößt damit gegen ISO 32000-2."));
                break;
            case PermsState.EncryptMetadataDiffers:
                AddProblem(Tr.T("Ob die Metadaten verschlüsselt sind, steht versiegelt anders als offen in der Datei; es gilt der versiegelte Wert."));
                break;
        }

        // the integrity protection, where the file has one or asks for one: checked when opened, a violation or a missing MAC said (No. 556)
        if (Security.Revision >= 6 && (StandardSecurityHandler.RequiresMac(Security.RawPermissions, Security.Revision) || Trailer.ContainsKey(PdfName.Get("AuthCode"))))
        {
            _cache.Clear();
            switch (Integrity.State)
            {
                case MacState.Invalid:
                    AddProblem(Tr.T("Integritätsschutz (MAC) verletzt: {0}", Integrity.Detail ?? ""));
                    break;
                case MacState.MissingButRequired:
                    AddProblem(Tr.T("Die Datei verlangt einen Integritätsschutz (MAC nach ISO/TS 32004), hat aber keinen — nicht normgerecht, die Unversehrtheit ist nicht nachweisbar."));
                    break;
            }
        }

        // Whatever was read before the key was known is still encrypted, except /Encrypt itself
        foreach (var number in _cache.Keys)
            if (number != _encryptNumber) _cache.TryRemove(number, out _);
    }

    /// <summary>
    /// Do the streams of the file decode with <paramref name="padded"/> and not with <paramref name="normal"/>? The first Flate-encoded streams
    /// stored in the file (at most eight, among its first 200 objects) are decrypted with both: the first one that inflates with either decides — with the key of the norm it
    /// stays (docs/FEHLERGEDAECHTNIS.md, No. 894).
    /// </summary>
    private bool DecodesOnlyWith(StandardSecurityHandler normal, StandardSecurityHandler padded)
    {
        int tried = 0, examined = 0;
        foreach (var (number, entry) in _xref.Entries.OrderBy(e => e.Key))
        {
            if (entry.Type != XRefEntryType.InFile || number == _encryptNumber) continue;
            if (++examined > 200) break;   // a large file: the first objects decide, opening stays quick
            PdfObject? loaded;
            try
            {
                loaded = TryLoad(number);
            }
            catch (PdfException)
            {
                continue;
            }
            if (loaded is not PdfStream { Id: { } streamId } stream || stream.RawData is not null || stream.DataLength < 8 ||
                stream.Dictionary.IsType(PdfName.XRef) || stream.Dictionary.GetRaw(PdfName.Get("F")) is not null ||
                StreamFilters.Chain(stream.Dictionary) is not [("FlateDecode", _)])
                continue;
            byte[] data;
            try
            {
                data = _source.ReadBytes(stream.DataOffset, stream.DataLength);
            }
            catch (Exception e) when (e is IOException or ArgumentException)
            {
                continue;
            }
            var withNormal = Inflates(normal.DecryptStream(streamId, data, PdfCipher.Rc4));
            var withPadded = Inflates(padded.DecryptStream(streamId, data, PdfCipher.Rc4));
            if (withNormal || withPadded) return withPadded && !withNormal;
            if (++tried == 8) break;
        }
        return false;

        static bool Inflates(byte[] bytes)
        {
            // a zlib header (RFC 1950: method 8, check bits) and a start that inflates
            if (bytes.Length < 3 || (bytes[0] & 0x0F) != 8 || (bytes[0] << 8 | bytes[1]) % 31 != 0) return false;
            try
            {
                using var z = new System.IO.Compression.DeflateStream(new MemoryStream(bytes, 2, bytes.Length - 2), System.IO.Compression.CompressionMode.Decompress);
                var buffer = new byte[256];
                return z.Read(buffer, 0, buffer.Length) > 0;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }
    }

    /// <summary>The trailer comes from the cross-references without a resolver; attaches one so that references in it (/Root, /Info …) can be resolved.</summary>
    private void AttachResolver(PdfObject obj)
    {
        switch (obj)
        {
            case PdfDictionary d:
                d.Resolver = this;
                foreach (var (_, v) in d.Entries) AttachResolver(v);
                break;
            case PdfArray a:
                a.Resolver = this;
                foreach (var v in a) AttachResolver(v);
                break;
        }
    }

    private XRefTable Rebuild(string problem)
    {
        AddProblem(problem);
        var table = XRefRebuilder.Rebuild(_source);
        if (table.Entries.Count == 0) throw new PdfException(Tr.T("Die Datei enthält keine lesbaren Objekte."));
        return table;
    }

    /// <summary>
    /// After a rebuild: registers the contents of the object streams found (may need the decryption key). As the rebuild takes the last
    /// "n g obj" of a number, the definition further back in the file counts (a later update, ISO 32000-2, 7.5.6): an object stream after
    /// an older one, or after an object of the same number written in the file, replaces it. Before, the first definition found stayed — after
    /// a rebuild the older revision of an incrementally updated file came back (docs/FEHLERGEDAECHTNIS.md, No. 887).
    /// An object stream that is being read on this thread (its own lookup caused the rebuild) is kept for later: read now, it came back as
    /// null and its objects counted as missing (docs/FEHLERGEDAECHTNIS.md, No. 923).
    /// </summary>
    /// <returns>false if an object stream was kept for the next lookup.</returns>
    private bool IndexObjectStreams(XRefTable table)
    {
        long At(int stream) => table.Entries.TryGetValue(stream, out var e) && e.Type == XRefEntryType.InFile ? e.Offset : long.MaxValue;
        // Clear the list first: reading the object streams leads back here
        var pending = table.ObjectStreams.Distinct().OrderBy(At).ToList();
        table.ObjectStreams.Clear();
        var later = new List<int>();
        foreach (var number in pending)
        {
            if (_resolving?.Contains(number) == true || t_loadingStreams?.Contains((this, number)) == true)
            {
                later.Add(number);
                continue;
            }
            try
            {
                if (GetObjectStream(number) is not { } stm) continue;
                var streamAt = At(number);
                var seen = new HashSet<int>();
                for (var i = 0; i < stm.Header.Count; i++)
                {
                    var contained = stm.Header[i].Number;
                    // the first of a number in one stream (as the stream reads it), never the stream itself
                    if (contained == number || !seen.Add(contained)) continue;
                    if (table.Entries.TryGetValue(contained, out var inFile) && inFile.Type == XRefEntryType.InFile && inFile.Offset > streamAt) continue;
                    // a stream kept for later does not replace what a stream further back in the file holds
                    if (_indexed is not null && _indexed.TryGetValue(contained, out var known) && At((int)known.Offset) > streamAt) continue;
                    (_indexed ??= new())[contained] = new XRefEntry(XRefEntryType.Compressed, number, i);
                }
            }
            catch (PdfException)
            {
            }
        }
        table.ObjectStreams.AddRange(later);
        return later.Count == 0;
    }

    /// <summary>AES-GCM: a part did not decrypt (its tag does not match) — read as empty, never as garbage (docs/FEHLERGEDAECHTNIS.md, No. 555).</summary>
    private void Tampered() => AddProblem(Tr.T("Teile der Datei wurden nach dem Verschlüsseln verändert (Prüfsumme passt nicht) — sie bleiben leer."));

    internal void AddProblem(string problem)
    {
        lock (_problems)
            if (!_problems.Contains(problem)) _problems.Add(problem);
    }

    private static int CompareVersions(string a, string b) =>
        double.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, out var x) &&
        double.TryParse(b, System.Globalization.CultureInfo.InvariantCulture, out var y)
            ? x.CompareTo(y)
            : string.CompareOrdinal(a, b);

    // ------------------------------------------------------------ Objects

    /// <summary>Resolves an indirect reference; the generation number is ignored.</summary>
    /// <param name="id">The object identifier.</param>
    /// <returns>The object, or <see cref="PdfNull"/> if it is missing or unreadable.</returns>
    public PdfObject Resolve(PdfObjectId id) => GetObject(id.Number);

    /// <summary>Returns the indirect object with number <paramref name="number"/>.</summary>
    /// <param name="number">The object number.</param>
    /// <returns>The object, or <see cref="PdfNull"/> if it is missing, unreadable or currently being resolved on this thread
    /// (a reference loop).</returns>
    /// <exception cref="PdfException">Objects refer to each other more than <see cref="PdfLimits.MaxResolveDepth"/> levels deep.</exception>
    /// <remarks>Thread-safe; every thread receives the same instance of an object. A missing entry can trigger a one-time
    /// rebuild of the cross-references.</remarks>
    public PdfObject GetObject(int number)
    {
        if (_cache.TryGetValue(number, out var cached)) return cached;
        // Add the object streams to the table once. Other threads wait until this is finished; otherwise they would
        // read the table while it is still being filled and cache "missing".
        if (!_objectStreamsIndexed)
            lock (_repairLock)
            {
                if (!_objectStreamsIndexed && !_indexingObjectStreams)
                {
                    _indexingObjectStreams = true;
                    try
                    {
                        _objectStreamsIndexed = IndexObjectStreams(_xref);
                    }
                    finally
                    {
                        _indexingObjectStreams = false;
                    }
                }
            }

        // Catch loops (for example, the /Length of a stream points into its own object stream)
        _resolving ??= [];
        if (_resolving.Count >= PdfLimits.MaxResolveDepth)
            throw new PdfException(Tr.T("Objekte verweisen zu tief aufeinander."));
        if (!_resolving.Add(number)) return PdfNull.Instance;
        try
        {
            // Two threads can read the same object at the same time: all of them get the same instance
            // (fonts, colour spaces … are cached by object identity)
            var value = Load(number);
            // an object that consists of nothing but a reference stands for its target; such chains are allowed (ISO 32000-2, 7.3.10) — handed out
            // as a reference, a dictionary or page behind it counted as missing (docs/FEHLERGEDAECHTNIS.md, No. 727). A loop ends as null above.
            if (value is PdfReference next) value = GetObject(next.Id.Number);
            return _cache.GetOrAdd(number, value);
        }
        finally
        {
            _resolving.Remove(number);
        }
    }

    private PdfObject Load(int number)
    {
        var loaded = TryLoad(number);
        if (loaded is not null) return loaded;
        if (_xref.Rebuilt) return PdfNull.Instance;
        // A reference to a number that does not exist according to /Size is null by the standard (7.3.10); no
        // reason to scan the whole file again (that takes seconds for large drawings)
        if (!_xref.Entries.ContainsKey(number) && _xref.Trailer.GetInteger(PdfName.Size) is { } size && number >= size)
            return PdfNull.Instance;

        // Entry missing or pointing nowhere: rebuild the table once, then try again
        lock (_repairLock)
        {
            if (!_xref.Rebuilt)
            {
                var table = Rebuild(Tr.T("Querverweise zeigen auf falsche Stellen — aus der Datei rekonstruiert."));
                _xref = MergeRebuilt(table);
                AttachResolver(_xref.Trailer);
                ForgetObjectStreams();
                // the objects in object streams are registered before the object is looked up again: before, only at the next lookup — the
                // object that caused the rebuild stayed missing (cached as null), though an object stream held it (test file
                // FRC_3.5_V_5_CFM_AESV3: a note lost its annotation; docs/FEHLERGEDAECHTNIS.md, No. 887)
                _indexingObjectStreams = true;
                try
                {
                    // an object stream being read on this thread follows at the next lookup (No. 923)
                    _objectStreamsIndexed = IndexObjectStreams(_xref);
                }
                finally
                {
                    _indexingObjectStreams = false;
                }
            }
        }
        return TryLoad(number) ?? PdfNull.Instance;
    }

    /// <summary>The rebuilt table takes over the previous trailer entries if the new trailer has no /Root.</summary>
    private XRefTable MergeRebuilt(XRefTable rebuilt)
    {
        if (rebuilt.Trailer.GetRaw(PdfName.Root) is null)
            foreach (var (k, v) in _xref.Trailer.Entries) rebuilt.Trailer.Set(k, v);
        return rebuilt;
    }

    private PdfObject? TryLoad(int number)
    {
        // after a rebuild, what an object stream further back in the file holds counts before the earlier definition (IndexObjectStreams)
        if ((_indexed is null || !_indexed.TryGetValue(number, out var entry)) && !_xref.Entries.TryGetValue(number, out entry))
            return _xref.IsFree(number) ? PdfNull.Instance : null;   // a free entry kept as a bit (No. 744): deleted, not missing
        switch (entry.Type)
        {
            case XRefEntryType.InFile:
                var parser = new PdfParser(new PdfLexer(_source), this, DecryptStringFor());
                (PdfObjectId Id, PdfObject Value)? parsed;
                try
                {
                    // offsets count from the "%" of "%PDF-" (ISO 32000-2, 7.5.2); then, as some writers count them, from the start of the file —
                    // whichever holds the right object (before, a wrong object at the first place made the right one count as missing; No. 747)
                    parsed = null;
                    if (_headerOffset > 0)
                    {
                        try
                        {
                            parsed = parser.ParseIndirectObject(entry.Offset + _headerOffset);
                        }
                        catch (PdfException ex) when (ex is not PdfLimitException)
                        {
                        }
                    }
                    if (parsed is not { } found || found.Id.Number != number) parsed = parser.ParseIndirectObject(entry.Offset);
                }
                catch (PdfLimitException)
                {
                    throw;
                }
                catch (PdfException)
                {
                    return null;
                }
                return parsed is { } p && p.Id.Number == number ? p.Value : null;

            case XRefEntryType.Compressed:
                var stm = GetObjectStream((int)entry.Offset);
                if (stm is null) return null;
                var index = entry.Generation;
                // The index can be wrong: then search by the number in the header
                if (index >= stm.Header.Count || stm.Header[index].Number != number)
                    index = stm.IndexOf(number);
                if (index < 0) return null;
                var at = stm.First + stm.Header[index].Offset;
                var value = new PdfParser(new PdfLexer(stm.Source, at), this).ParseObject();
                // an object ends before the next one begins — white space between them is not required (ISO 32000-2, 7.5.7, corrected in
                // 2020): a number, name, true, false or null would run into a following one, so these are read again up to the next offset
                if (value is PdfInteger or PdfReal or PdfName or PdfBoolean or PdfNull && index + 1 < stm.Header.Count
                    && stm.Header[index + 1].Offset > stm.Header[index].Offset
                    && new PdfParser(new PdfLexer(stm.Source.Prefix(stm.First + stm.Header[index + 1].Offset), at), this).ParseObject() is { } bounded)
                    value = bounded;
                return value;

            default:
                return PdfNull.Instance;
        }
    }

    private Func<PdfObjectId, byte[], byte[]>? DecryptStringFor()
    {
        if (Security is not { } security) return null;
        var encryptNumber = _encryptNumber;
        return (id, bytes) => id.Number == encryptNumber ? bytes : security.DecryptString(id, bytes);
    }

    private sealed record ObjectStream(PdfSource Source, long First, List<(int Number, long Offset)> Header)
    {
        private Dictionary<int, int>? _byNumber;

        /// <summary>Length of the decoded data (what keeping the stream costs).</summary>
        public long Bytes { get; init; }

        /// <summary>When it was last used (a counter of the document): the least recently used is dropped first.</summary>
        public long LastUse;

        /// <summary>Position in the header by object number, built once when the cross-references have wrong indices (a linear
        /// search through the whole header per object takes 20 s for 200,000 objects).</summary>
        public int IndexOf(int number)
        {
            var map = Volatile.Read(ref _byNumber);
            if (map is null)
            {
                map = new Dictionary<int, int>(Header.Count);
                for (var i = 0; i < Header.Count; i++) map.TryAdd(Header[i].Number, i);
                Volatile.Write(ref _byNumber, map);
            }
            return map.TryGetValue(number, out var index) ? index : -1;
        }
    }

    private ObjectStream? GetObjectStream(int number)
    {
        // Lazy: concurrent accesses decode an object stream only once
        if (!_objectStreams.TryGetValue(number, out var lazy))
            lazy = _objectStreams.GetOrAdd(number, n => new Lazy<ObjectStream?>(() => LoadObjectStream(n)));
        // An object stream that lies within itself (damaged or malicious): detected here instead of through Lazy's exception
        if (!lazy.IsValueCreated && t_loadingStreams?.Contains((this, number)) == true) return null;
        try
        {
            var stream = lazy.Value;
            if (stream is not null) Volatile.Write(ref stream.LastUse, Interlocked.Increment(ref _objectStreamClock));
            return stream;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // Per thread: object streams currently being loaded
    [ThreadStatic] private static HashSet<(PdfDocument, int)>? t_loadingStreams;

    private ObjectStream? LoadObjectStream(int number)
    {
        var loading = t_loadingStreams ??= [];
        loading.Add((this, number));
        try
        {
            if (GetObject(number) is not PdfStream stream) return null;
            // decoded over and over (more than the budget holds, again and again): no more — a crafted file, not a real one (No. 745)
            if (Interlocked.Read(ref _objectStreamsDecoded) > ObjectStreamWork) return null;
            var data = GetStreamData(stream);
            Interlocked.Add(ref _objectStreamsDecoded, data.Length);
            var loaded = new ObjectStream(PdfSource.FromBytes(data), stream.Dictionary.GetInteger(PdfName.First) ?? 0,
                ObjectStreamReader.Header(data, stream.Dictionary)) { Bytes = data.Length, LastUse = Interlocked.Increment(ref _objectStreamClock) };
            Keep(number, data.Length);
            return loaded;
        }
        finally
        {
            loading.Remove((this, number));
        }
    }

    /// <summary>
    /// Most bytes of decoded object streams the document keeps: <see cref="PdfLimits.Budget"/> with at most 1 GB and at least 128 MB —
    /// real files rarely need more than a few megabytes (docs/FEHLERGEDAECHTNIS.md, No. 745). Tests set a smaller value.
    /// </summary>
    internal long ObjectStreamBudget { get; set; } = PdfLimits.Budget(1L << 30, 128L << 20);

    /// <summary>Most bytes of object streams decoded in all (dropped ones decoded again included): eight budgets, at least 64 times the file.</summary>
    private long ObjectStreamWork => Math.Max(8 * ObjectStreamBudget, 64 * _source.Length);

    /// <summary>Bytes of decoded object streams kept at the moment (tests).</summary>
    internal long ObjectStreamBytesKept => Interlocked.Read(ref _objectStreamBytes);

    /// <summary>Bytes of object streams decoded in all (tests).</summary>
    internal long ObjectStreamBytesDecoded => Interlocked.Read(ref _objectStreamsDecoded);

    /// <summary>
    /// Counts a newly decoded object stream; over the budget, drops the least recently used others down to three quarters of it (in one go,
    /// so that a file with many streams does not search on every load).
    /// </summary>
    private void Keep(int number, long bytes)
    {
        if (Interlocked.Add(ref _objectStreamBytes, bytes) <= ObjectStreamBudget) return;
        lock (_objectStreamEviction)
        {
            if (Interlocked.Read(ref _objectStreamBytes) <= ObjectStreamBudget) return;
            var kept = new List<(int Number, ObjectStream Stream)>();
            foreach (var (key, lazy) in _objectStreams)
                if (key != number && lazy.IsValueCreated && lazy.Value is { } loaded) kept.Add((key, loaded));
            foreach (var (key, loaded) in kept.OrderBy(k => Volatile.Read(ref k.Stream.LastUse)))
            {
                if (Interlocked.Read(ref _objectStreamBytes) <= ObjectStreamBudget / 4 * 3) break;
                if (_objectStreams.TryRemove(key, out _)) Interlocked.Add(ref _objectStreamBytes, -loaded.Bytes);
            }
        }
    }

    /// <summary>Drops all decoded object streams (after a repair, or when the key changes).</summary>
    private void ForgetObjectStreams()
    {
        lock (_objectStreamEviction)
        {
            _objectStreams.Clear();
            Interlocked.Exchange(ref _objectStreamBytes, 0);
        }
    }

    // ------------------------------------------------------------ Streams

    /// <summary>Returns the raw stream data, decrypted but with the filters not yet applied.</summary>
    /// <param name="stream">The stream.</param>
    /// <returns>The decrypted, still encoded data.</returns>
    /// <exception cref="PdfException">An attachment encrypted with a key that is missing (only the attachments are encrypted and not unlocked).</exception>
    public byte[] GetRawStreamData(PdfStream stream)
    {
        var data = stream.RawData ?? _source.ReadBytes(stream.DataOffset, stream.DataLength);
        var cipher = StreamCipherFor(stream);
        return cipher == PdfCipher.None ? data : Security!.DecryptStream(stream.Id!.Value, data, cipher);
    }

    /// <summary>True if the data is stored unencrypted in the file (or in memory); stored and raw data are then identical.</summary>
    internal bool IsStoredPlain(PdfStream stream) => StreamCipherFor(stream) == PdfCipher.None;

    /// <summary>
    /// The method a stream is encrypted with (ISO 32000-2, 7.4.10 and 7.6.6): its own crypt filter (/Crypt as the first filter, by its
    /// /Name), embedded files by /EFF, metadata not without /EncryptMetadata, everything else by /StmF. Before, every stream was read with
    /// /StmF: a form with a filter of its own came as garbage, an attachment of a file that encrypts only its attachments stayed encrypted,
    /// and the question before redacting searched the encrypted bytes (docs/FEHLERGEDAECHTNIS.md, No. 442).
    /// </summary>
    internal PdfCipher StreamCipherFor(PdfStream stream)
    {
        if (Security is not { } security || stream.RawData is not null || stream.Id is null) return PdfCipher.None;
        var dict = stream.Dictionary;
        if (dict.IsType(PdfName.XRef)) return PdfCipher.None;   // cross-reference streams are never encrypted
        // nor the contents of a stream that refers to an external file (7.6.2; docs/FEHLERGEDAECHTNIS.md, No. 751)
        if (dict.GetRaw(PdfName.Get("F")) is not null) return PdfCipher.None;
        if (StreamFilters.Chain(dict) is [("Crypt", var parms), ..])
        {
            var name = parms?.GetName(PdfName.Name);
            if (security.CipherNamed(name) is { } own) return own;
            // every filter used must be in /CF (Table 20); read as an ordinary stream, as before, and say so
            AddProblem(Tr.T("Ein Datenstrom nennt einen Kryptfilter, den die Verschlüsselung nicht kennt („{0}“).", name?.Value ?? "?"));
            return security.StreamCipher;
        }
        // /EncryptMetadata false leaves only the document's metadata plain — that of pages and images is encrypted as any stream (ISO 32000-2,
        // Table 20; docs/FEHLERGEDAECHTNIS.md, No. 551). Tolerated: another metadata stream that is stored plain (its data start as XMP)
        if (!security.EncryptMetadata && dict.IsType(PdfName.Metadata))
        {
            if ((Catalog?.GetRaw(PdfName.Metadata) as PdfReference)?.Id.Number == stream.Id.Value.Number) return PdfCipher.None;
            if (StoredAsXmp(stream))
            {
                AddProblem(Tr.T("Metadaten einer Seite oder eines Bildes sind unverschlüsselt gespeichert, obwohl nur die des Dokuments es sein dürfen — so gelesen, wie sie dastehen."));
                return PdfCipher.None;
            }
        }
        if (security.EmbeddedFileCipher != security.StreamCipher && !dict.IsType(PdfName.Get("ObjStm")) && IsEmbeddedFile(stream))
            return security.EmbeddedFileCipher;
        return security.StreamCipher;
    }

    /// <summary>Whether the stored bytes of a stream (no filter) start as XMP: „&lt;?xpacket“ or „&lt;x:xmpmeta“, after white space or a byte order mark.</summary>
    private bool StoredAsXmp(PdfStream stream)
    {
        if (stream.Dictionary.ContainsKey(PdfName.Filter) || stream.DataLength < 10) return false;
        var head = _source.ReadBytes(stream.DataOffset, (int)Math.Min(stream.DataLength, 64)).AsSpan();
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) head = head[3..];
        head = head.TrimStart(" \t\r\n"u8);
        return head.StartsWith("<?xpacket"u8) || head.StartsWith("<x:xmpmeta"u8);
    }

    private HashSet<int>? _embeddedFiles;
    private bool _collectingEmbeddedFiles;

    /// <summary>
    /// Whether a stream is an embedded file: /Type /EmbeddedFile, or a stream that an /EF or /RF of a file specification names (/Type is
    /// optional there). Found once, following every reference from the catalogue — only for files whose embedded files are encrypted
    /// otherwise than their streams.
    /// </summary>
    private bool IsEmbeddedFile(PdfStream stream)
    {
        if (stream.Dictionary.IsType(PdfName.Get("EmbeddedFile"))) return true;
        if (_collectingEmbeddedFiles) return false;   // objects read while collecting (an object stream) are never attachments
        if (_embeddedFiles is null)
        {
            _collectingEmbeddedFiles = true;
            try
            {
                _embeddedFiles = CollectEmbeddedFiles();
            }
            finally
            {
                _collectingEmbeddedFiles = false;
            }
        }
        return _embeddedFiles.Contains(stream.Id!.Value.Number);
    }

    private HashSet<int> CollectEmbeddedFiles()
    {
        var found = new HashSet<int>();
        var queue = new Queue<int>();
        var seen = new HashSet<int>();
        void Take(PdfObject? obj)
        {
            if (obj is PdfReference r) found.Add(r.Id.Number);
        }
        void Walk(PdfObject obj, int depth)
        {
            if (depth > 64) return;
            switch (obj)
            {
                case PdfReference r:
                    if (seen.Count < 8_388_608 && seen.Add(r.Id.Number)) queue.Enqueue(r.Id.Number);
                    return;
                case PdfStream s:
                    Walk(s.Dictionary, depth + 1);
                    return;
                case PdfArray array:
                    for (var i = 0; i < array.Count; i++) Walk(array[i], depth + 1);
                    return;
                case PdfDictionary dict:
                    if (dict.GetRaw(PdfName.Get("EF")) is var ef && (ef is PdfDictionary || ef is PdfReference)
                        && (ef as PdfDictionary ?? dict.GetDictionary(PdfName.Get("EF"))) is { } files)
                        foreach (var (key, _) in files.Entries) Take(files.GetRaw(key));
                    if (dict.GetDictionary(PdfName.Get("RF")) is { } related)
                        foreach (var (key, _) in related.Entries)
                            if (related.Get(key) is PdfArray list)
                                for (var i = 0; i < list.Count; i++) Take(list[i]);
                    foreach (var (_, value) in dict.Entries) Walk(value, depth + 1);
                    return;
            }
        }
        if (Trailer.GetRaw(PdfName.Root) is { } root) Walk(root, 0);
        while (queue.TryDequeue(out var number))
        {
            try
            {
                Walk(GetObject(number), 0);
            }
            catch (PdfException)
            {
                // an object that cannot be read holds no file specification
            }
        }
        return found;
    }

    /// <summary>Stored data without a copy (valid while the document is open), for writing large streams.</summary>
    internal ReadOnlySpan<byte> StoredStreamData(PdfStream stream) =>
        stream.RawData ?? _source.Span(stream.DataOffset, stream.DataLength);

    /// <summary>Number of the /Encrypt object (never copied as an ordinary object when saving).</summary>
    internal int? EncryptNumber => _encryptNumber;

    private static readonly PdfName DecodedLength = PdfName.Get("DL");

    /// <summary>
    /// Decrypts and decodes a stream. If the filter chain ends with an image format (JPEG …), the result states
    /// which one, and the data is still in that format.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <param name="sizeHint">Expected decoded size in bytes, if known (images); avoids copying. If 0, /DL is used when present.</param>
    /// <returns>The decoded data and its format; a damaged stream yields the readable part and adds a problem to <see cref="Problems"/>.</returns>
    public StreamFilters.DecodeResult DecodeStream(PdfStream stream, int sizeHint = 0)
    {
        // PDF 2.0: /DL gives the decoded length
        if (sizeHint <= 0 && stream.Dictionary.GetInteger(DecodedLength) is > 0 and <= int.MaxValue and var dl) sizeHint = (int)dl;
        var result = StreamFilters.Decode(GetRawStreamData(stream), stream.Dictionary, sizeHint);
        if (result.Truncated)
            AddProblem(Tr.T("Mindestens ein Datenstrom ist beschädigt und nur teilweise lesbar."));
        return result;
    }

    /// <summary>Decrypts and decodes into a pooled buffer (content streams).</summary>
    /// <param name="stream">The stream.</param>
    /// <returns>The pooled data; the caller must release it after use.</returns>
    public PooledBytes DecodeStreamPooled(PdfStream stream)
    {
        var result = StreamFilters.DecodePooled(GetRawStreamData(stream), stream.Dictionary, out var truncated);
        if (truncated)
            AddProblem(Tr.T("Mindestens ein Datenstrom ist beschädigt und nur teilweise lesbar."));
        return result;
    }

    /// <summary>Decrypts and decodes a stream; for image formats, the data stays in that format.</summary>
    /// <param name="stream">The stream.</param>
    /// <returns>The decoded data.</returns>
    public byte[] GetStreamData(PdfStream stream) => DecodeStream(stream).Data;

    // ------------------------------------------------------------ Pages

    private List<PdfPage> LoadPages()
    {
        var pages = new List<PdfPage>();
        var root = Catalog?.GetRaw(PdfName.Pages);
        var visited = new Visited([], new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance));
        if (root is not null) Walk(root, null, null, null, null, 0, pages, visited);

        if (pages.Count == 0)
        {
            // Page tree damaged: all objects with /Type /Page in object number order
            AddProblem(Tr.T("Seitenbaum defekt — Seiten aus der Datei zusammengesucht."));
            foreach (var number in ObjectNumbers)
            {
                if (GetObject(number) is PdfDictionary d && d.IsType(PdfName.Page))
                {
                    // Inherited attributes via /Parent (test file issue9418: fonts only on the parent node; without this the page
                    // shows no text, while the saved copy does, because the writer follows /Parent)
                    var (resources, mediaBox, cropBox, rotate) = Inherited(d);
                    pages.Add(MakePage(pages.Count, d, new PdfObjectId(number, 0), resources, mediaBox, cropBox, rotate));
                }
            }
        }
        return pages;
    }

    /// <summary>Inheritable page attributes along /Parent; the nearer node wins (for pages outside an intact page tree).</summary>
    private (PdfDictionary? Resources, PdfRectangle? MediaBox, PdfRectangle? CropBox, int? Rotate) Inherited(PdfDictionary page)
    {
        PdfDictionary? resources = null;
        PdfRectangle? mediaBox = null, cropBox = null;
        int? rotate = null;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (PdfDictionary? node = page; node is not null && seen.Add(node) && seen.Count <= PdfLimits.MaxPageTreeDepth; node = node.GetDictionary(ParentName))
        {
            resources ??= node.GetDictionary(PdfName.Resources);
            mediaBox ??= PdfRectangle.From(node.Get(PdfName.MediaBox));
            cropBox ??= PdfRectangle.From(node.Get(PdfName.CropBox));
            rotate ??= (int?)node.GetInteger(PdfName.Rotate);
        }
        return (resources, mediaBox, cropBox, rotate);
    }

    private static readonly PdfName ParentName = PdfName.Get("Parent");

    private void Walk(PdfObject node, PdfDictionary? resources, PdfRectangle? mediaBox, PdfRectangle? cropBox, int? rotate,
        int depth, List<PdfPage> pages, Visited visited)
    {
        if (depth > PdfLimits.MaxPageTreeDepth) return;
        PdfObjectId? id = node is PdfReference r ? r.Id : null;
        if (id is { } i && !visited.Ids.Add(i)) return;   // loop in the page tree
        if ((id is { } rid ? Resolve(rid) : node) is not PdfDictionary dict) return;
        if (!visited.Nodes.Add(dict)) return;

        // Inherited attributes: the nearer node wins
        resources = dict.GetDictionary(PdfName.Resources) ?? resources;
        mediaBox = PdfRectangle.From(dict.Get(PdfName.MediaBox)) ?? mediaBox;
        cropBox = PdfRectangle.From(dict.Get(PdfName.CropBox)) ?? cropBox;
        rotate = (int?)dict.GetInteger(PdfName.Rotate) ?? rotate;

        var kids = dict.GetArray(PdfName.Kids);
        var isPage = dict.IsType(PdfName.Page) || (kids is null && !dict.IsType(PdfName.Pages));
        if (isPage)
        {
            pages.Add(MakePage(pages.Count, dict, id, resources, mediaBox, cropBox, rotate));
            return;
        }
        if (kids is null) return;
        for (var k = 0; k < kids.Count; k++)
            Walk(kids[k], resources, mediaBox, cropBox, rotate, depth + 1, pages, visited);
    }

    private sealed record Visited(HashSet<PdfObjectId> Ids, HashSet<PdfDictionary> Nodes);

    private PdfPage MakePage(int index, PdfDictionary dict, PdfObjectId? id, PdfDictionary? resources,
        PdfRectangle? mediaBox, PdfRectangle? cropBox, int? rotate)
    {
        resources ??= dict.GetDictionary(PdfName.Resources);
        mediaBox ??= PdfRectangle.From(dict.Get(PdfName.MediaBox));
        cropBox ??= PdfRectangle.From(dict.Get(PdfName.CropBox));
        rotate ??= (int?)dict.GetInteger(PdfName.Rotate);
        if (mediaBox is null) AddProblem(Tr.T("Mindestens eine Seite hat keine gültige Größe — Letter angenommen."));
        return new PdfPage(index, dict, id, resources, mediaBox ?? PdfPage.DefaultMediaBox, cropBox, rotate ?? 0);
    }

    /// <summary>Releases the underlying source (file mapping or memory).</summary>
    public void Dispose()
    {
        // a handler shared with other documents (OpenWritten) would otherwise keep this one alive through its event (quality round of phase 5)
        if (Security is { } security) security.AuthenticationFailed -= Tampered;
        _source.Dispose();
    }
}
