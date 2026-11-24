using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Platform;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>Options for saving.</summary>
public sealed record PdfSaveOptions
{
    /// <summary>PDF version of the new file ("1.4", "1.7", "2.0" …). From 1.5 with object and cross-reference streams.</summary>
    /// <remarks>Encryption can require a higher version; see <see cref="PdfSaveResult.VersionRaised"/>.</remarks>
    public string Version { get; init; } = "1.7";

    /// <summary>Keep the encryption of the document (same passwords and permissions).</summary>
    public bool KeepEncryption { get; init; } = true;

    /// <summary>
    /// With <see cref="KeepEncryption"/>: take the encryption (and the first part of /ID, on which the key depends) from this
    /// file instead of the main document, for when the pages come from an intermediate file (save as image).
    /// </summary>
    public PdfDocument? EncryptionFrom { get; init; }

    /// <summary>
    /// <see cref="KeepEncryption"/> with all permissions (removing restrictions, see KONZEPT.md, section 5.11): the same
    /// passwords, with all permissions granted (<see cref="StandardSecurityHandler.WithAllPermissions"/>).
    /// </summary>
    public bool GrantAllPermissions { get; init; }

    /// <summary>
    /// Writes a fresh MAC (ISO/TS 32004) into a copy that keeps the encryption of a file with a MAC, instead of dropping the requirement
    /// (docs/ABLAUFPLAN.md, 5.5 b). Off until an outside check with a tool that checks TS 32004 has been passed (a wrong MAC is worse than
    /// none); on for the tests, and with PDFKNEAD_MAC_SCHREIBEN=1 for trying it by hand.
    /// </summary>
    internal bool WriteMac { get; init; } = PdfMac.WritingOn;

    /// <summary>From PDF 1.5, small objects in object streams and cross-references as a stream (smaller); off for very old programs.</summary>
    public bool ObjectStreams { get; init; } = true;

    /// <summary>Pack unencoded streams with Flate (lossless).</summary>
    public bool CompressStreams { get; init; } = true;

    /// <summary>
    /// Flatten: form fields, comments and stamps (everything with an appearance) become fixed page content; afterwards
    /// nothing can be filled in or moved any more. Links stay.
    /// </summary>
    public bool Flatten { get; init; }

    /// <summary>
    /// For printing (with <see cref="Flatten"/>): only what is printed, that is, annotations with the Print flag (12.5.3), no
    /// fields "do not print" or "hidden"; "print only" (invisible on screen) is included.
    /// </summary>
    public bool ForPrint { get; init; }

    /// <summary>
    /// New protection (replaces an existing encryption); null = <see cref="KeepEncryption"/> decides.
    /// Without protection and without <see cref="KeepEncryption"/>, the file is written unencrypted.
    /// </summary>
    public PdfProtection? Protection { get; init; }

    /// <summary>
    /// New encryption for recipients (a public-key handler, ISO 32000-2, 7.6.5: adbe.pkcs7.s5 with 256-bit AES) instead of passwords; takes the
    /// place of <see cref="Protection"/> and of a kept encryption. Null or empty: <see cref="Protection"/> and <see cref="KeepEncryption"/>
    /// decide (docs/FEHLERGEDAECHTNIS.md, No. 1117).
    /// </summary>
    public IReadOnlyList<PdfRecipientGroup>? Recipients { get; init; }

    /// <summary>Hidden data that is dropped when saving (see <see cref="DocumentCleanup"/>).</summary>
    public CleanupItems Cleanup { get; init; }

    /// <summary>
    /// The file holds some pages of the document (extracting, splitting): only what belongs to those pages goes along. The attachments of
    /// the whole document (name tree, the catalogue's /AF — such as the source of the whole document) only with
    /// <see cref="KeepDocumentAttachments"/>; the document's XMP slim (title and description stay; history, identifiers of the whole and
    /// its preview go); the catalogue's private data (/PieceInfo) goes (decided on 3.11.2026; docs/FEHLERGEDAECHTNIS.md,
    /// No. 447).
    /// </summary>
    public bool PartOfDocument { get; init; }

    /// <summary>With <see cref="PartOfDocument"/>: the attachments of the whole document go along (when chosen so).</summary>
    public bool KeepDocumentAttachments { get; init; }

    /// <summary>
    /// With <see cref="CleanupItems.Attachments"/>: embedded files that stay — their data streams (/EF /F or /UF) in the main document; the
    /// file specifications naming them stay in the name tree, in /AF and on file attachment annotations. Null or empty: all go (KONZEPT.md,
    /// section 5.24, decision (4) of 15.11.2026: an e-invoice is a row of its own when cleaning up).
    /// </summary>
    public IReadOnlySet<PdfObjectId>? KeptAttachments { get; init; }

    /// <summary>
    /// The XMP properties that describe attached files (<see cref="AttachedFileMetadata"/>) stay although attachments go: the files they
    /// describe are among <see cref="KeptAttachments"/>.
    /// </summary>
    public bool AttachedFileMetadataStays { get; init; }

    /// <summary>
    /// Hidden data dropped only on some pages, in addition to <see cref="Cleanup"/> (redacting with „nur auf Seiten mit Schwärzung“):
    /// only the items that belong to pages count (<see cref="DocumentCleanup.PageItems"/>); the layers themselves stay, other pages still
    /// use them. Null: none.
    /// </summary>
    public CleanupScope? ScopedCleanup { get; init; }

    /// <summary>
    /// Text layers of the text recognition to put over pages (<see cref="Ocr.OcrTextLayer.Form"/>), by page (index in the written order);
    /// each is drawn after the page's content in its default space. Null: none.
    /// </summary>
    public IReadOnlyDictionary<int, PdfStream>? TextLayers { get; init; }

    /// <summary>
    /// Further forms drawn over pages in their default space, after the content and the text layers (the reasons written into redaction bars,
    /// <see cref="Editing.RedactionReasons"/>), by page (index in the written order). Null: none.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<PdfStream>>? Overlays { get; init; }

    /// <summary>
    /// PDF/A-1 with its transparency flattened (<see cref="PdfAFlattening"/>): the images over the pages show what was translucent, so
    /// underneath everything is drawn opaque — graphics states without opacity, soft mask and blend mode, images without soft mask.
    /// </summary>
    internal bool PdfAFlattened { get; init; }

    /// <summary>PDF/A-1 flattened: the annotations (as read) whose look is in the images, written with an empty appearance and without /CA.</summary>
    internal IReadOnlySet<PdfDictionary>? PdfABlankAnnotations { get; init; }

    /// <summary>PDF/A level a: the language and the answers for the figures (<see cref="PdfATagging"/>); null: none given.</summary>
    internal PdfAAccessibility? PdfAAccessibility { get; init; }

    /// <summary>
    /// The title of the document (docs/FEHLERGEDAECHTNIS.md, No. 492): into /Info /Title (from PDF 2.0 only into the XMP, /Info is deprecated
    /// there) and as dc:title into the XMP — an existing packet is changed, a file without one gets a slim one. Null: as it is.
    /// </summary>
    public string? DocumentTitle { get; init; }

    /// <summary>The natural language of the document (/Lang of the catalog, a language tag such as „de-DE“; ISO 14289-1, 7.2). Null: as it is.</summary>
    public string? DocumentLanguage { get; init; }

    /// <summary>Readers show the title instead of the file name (/ViewerPreferences /DisplayDocTitle; ISO 14289-1, 7.1). Null: as it is.</summary>
    public bool? DisplayDocTitle { get; init; }

    /// <summary>
    /// Removes the PDF/UA identification and the accessibility declarations from the XMP, as a save that breaks accessibility does — the repair
    /// „Kennung entfernen“ of the check „barrierefrei?“ for a file whose claim it does not meet (docs/ABLAUFPLAN.md, 6.7 step 7).
    /// </summary>
    public bool RemoveAccessibilityClaim { get; init; }

    /// <summary>
    /// Every configuration of the layers gets a name (the default one „Standard“, others „Ansicht“, numbered where taken) — the repair
    /// „Ebenen-Ansichten benennen“ of the check „barrierefrei?“ (ISO 14289-1, 7.10; ISO 14289-2, 8.7; docs/ABLAUFPLAN.md, 6.7 step 7).
    /// </summary>
    public bool NameLayerConfigurations { get; init; }

    /// <summary>
    /// With <see cref="CleanupItems.Metadata"/>: the title stays (design decision: cleaning up no longer deletes the title; the box
    /// „Titel behalten“, default on). Author, program and the other properties go as before.
    /// </summary>
    public bool KeepTitle { get; init; } = true;

    /// <summary>Annotations to add to pages (the notes with the reason at redaction bars), by page (index in the written order). Null: none.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<PdfDictionary>>? AddedAnnotations { get; init; }

    /// <summary>
    /// The structure of the elements fixed into an untagged file made by PDF-Knead itself (the redaction protocol, docs/ABLAUFPLAN.md, 6.6 i,
    /// item 3): per element its role — „H1“, „P“, „Artifact“, or „TH n“/„TD n“ for a cell of table row n — in one Sect; the copy gets a new
    /// structure tree. Null: none.
    /// </summary>
    internal IReadOnlyDictionary<Editing.PageElement, string>? ElementStructure { get; init; }

    /// <summary>
    /// Merged files made by PDF-Knead itself as part of this document (the redaction protocol): their top elements join the top Document of
    /// the copy directly, not in a Part or DocumentFragment of their own (they come from no other document). Null: none.
    /// </summary>
    internal IReadOnlySet<Document.PdfDocument>? OwnParts { get; init; }

    /// <summary>
    /// Places of findings that stay although their item is cleaned up (the „x“ in „Dokument bereinigen“, KONZEPT.md, section 5.18); page
    /// indices in the written order. Null: none.
    /// </summary>
    public IReadOnlyList<CleanupKeep>? KeepPlaces { get; init; }

    /// <summary>
    /// Modification date (/ModDate) to use instead of the time of writing. The compressor writes the trial run and the save with
    /// the same date: otherwise the digits in the packed object stream would change the size by a byte, and the prediction would be off.
    /// </summary>
    public DateTimeOffset? ModificationTime { get; init; }

    /// <summary>Inserted files whose bookmarks are left out (window „Seiten zusammenführen“, option „Lesezeichen mitnehmen“ off).</summary>
    public IReadOnlySet<Document.PdfDocument>? OutlinesLeftOut { get; init; }

    /// <summary>
    /// Apply redactions (<see cref="Redactor"/>): the content under the areas is removed and black bars take its place.
    /// Otherwise redactions stay editable marks (working file).
    /// </summary>
    public bool ApplyRedactions { get; init; }

    /// <summary>
    /// With <see cref="ApplyRedactions"/>: hide the length of redacted words (docs/RECHERCHE-VERSTECKEN.md, B-13) — the gap in the line and the
    /// bar grow to the next step, the words after it move up, without any of them coming under a bar.
    /// </summary>
    public bool HideRedactedLengths { get; init; }

    /// <summary>
    /// New file identifier (/ID) instead of the original's, so that the copy cannot be linked to it (save as image; for
    /// redacted and sanitised copies this happens automatically). Exception: a kept encryption up to revision 4 needs the old one.
    /// </summary>
    public bool NewFileId { get; init; }

    /// <summary>
    /// Compression: merge duplicates, remove ballast, strongest packing level, object streams; with
    /// <see cref="CompressionSettings.ImageDpi"/> also downsample images. Null = no compression.
    /// </summary>
    public CompressionSettings? Compression { get; init; }

    /// <summary>Codec for image formats that the core cannot handle itself (JPEG), for redacting image pixels and for compression; without it, such images under a bar are dropped completely.</summary>
    public Graphics.IImageCodec? ImageCodec { get; init; }

    /// <summary>With <see cref="Compression"/>: cache across several runs over the same pages (trial runs, see <see cref="Writing.CompressionCache"/>).</summary>
    public CompressionCache? CompressionCache { get; init; }

    /// <summary>
    /// Write for PDF/A (KONZEPT.md, section 5.21): PDF 1.7 without encryption, an output intent, XMP metadata, and every object brought into
    /// line with ISO 19005-2 where that keeps the appearance. Whether the result conforms is up to <see cref="PdfACheck"/>; see
    /// <see cref="PdfAExport"/>.
    /// </summary>
    public PdfAConformance PdfA { get; init; }

}

/// <summary>
/// Passwords and permissions for saving. With an empty open password, the file opens without a password, and the
/// permissions are then only a request to the viewer (the key can be computed from the file).
/// </summary>
/// <param name="UserPassword">The open password; empty for none.</param>
/// <param name="OwnerPassword">The permissions password; if empty, the open password is used.</param>
/// <param name="Permissions">The permissions granted without the owner password.</param>
public sealed record PdfProtection(string UserPassword, string OwnerPassword, PdfPermissions Permissions)
{
    /// <summary>Method per version: up to PDF 1.5 RC4 128 bit, 1.6 AES-128, from 1.7 AES-256.</summary>
    /// <param name="version">The PDF version.</param>
    /// <returns>The cipher.</returns>
    public static PdfCipher CipherFor(string version) =>
        PdfWriter.CompareVersions(version, "1.6") < 0 ? PdfCipher.Rc4 : PdfWriter.CompareVersions(version, "1.7") < 0 ? PdfCipher.Aes128 : PdfCipher.Aes256;
}

/// <summary>
/// Result of saving.
/// </summary>
/// <param name="Version">The actual version written (encryption can require a higher one).</param>
/// <param name="Length">Size of the written file in bytes.</param>
/// <param name="Objects">Number of objects written.</param>
/// <param name="VersionRaised">True if the version was raised above the requested one.</param>
/// <param name="Security">The key with which the file was encrypted (null = unencrypted); with it, a trial run can be opened
/// without a password.</param>
public sealed record PdfSaveResult(string Version, long Length, int Objects, bool VersionRaised, StandardSecurityHandler? Security = null)
{
    /// <summary>
    /// Redacted pages still to be checked in the written file (<see cref="Redactor.CheckWritten"/>): by <see cref="PdfWriter.Write(PdfDocument,
    /// IReadOnlyList{PageItem}, Stream, PdfSaveOptions?, CancellationToken)"/> itself when it can read its output back, otherwise by
    /// <see cref="PdfFileSaver"/> before the file replaces the target. Null when nothing is left to check.
    /// </summary>
    internal (IReadOnlyList<(int Page, Redactor.WrittenCheck Check)> Pages, bool ContentAsRedacted)? RedactionChecks { get; init; }

    /// <summary>
    /// With long-term validation: the certificates (subject names) for which no revocation status could be obtained, so their signatures
    /// can be checked later only while the certification authority still answers.
    /// </summary>
    public IReadOnlyList<string> LongTermMissing { get; init; } = [];

    /// <summary>Pages (index in the written order) whose marks of hidden content found nothing left to remove: said to the user, never silently.</summary>
    public IReadOnlyList<int> IdleHiddenMarks { get; init; } = [];

    /// <summary>Pages (index in the written order) where the length of a redacted word could only partly be hidden (not enough room in its line).</summary>
    public IReadOnlyList<int> LengthsPartlyShown { get; init; } = [];

    /// <summary>Pages (index in the written order) with images whose hidden parts could not be cut away (<see cref="CleanupItems.HiddenImageParts"/>).</summary>
    public IReadOnlyList<int> ImagePartsKept { get; init; } = [];

    /// <summary>
    /// The source was protected against changes by a MAC (ISO/TS 32004) and the copy is not: PDF Knead cannot write the MAC yet — the copy
    /// stays encrypted (where it was kept) with the same passwords and rights, without the protection. Said after saving (No. 546).
    /// </summary>
    public bool IntegrityProtectionDropped { get; init; }

    /// <summary>
    /// The source was identified as PDF/A and the copy surely is not (encrypted, Brotli, a version outside the part): the identification was
    /// removed — what broke it, e.g. „Passwortschutz“ (No. 554).
    /// </summary>
    public string? PdfAClaimRemovedFor { get; init; }

    /// <summary>
    /// The source claimed accessibility (PDF/UA identification or an accessibility declaration in its XMP) and a change in this save broke it:
    /// the claims were removed — said after saving, not asked before (docs/ABLAUFPLAN.md, 6.1; decision G).
    /// </summary>
    public bool AccessibilityClaimRemoved { get; init; }

    /// <summary>What broke the accessibility of a tagged file in this save (translated, e.g. „Schwärzen“); empty if nothing did.</summary>
    public IReadOnlyList<string> AccessibilityBreaks { get; init; } = [];

    /// <summary>
    /// PDF/A-2 or -3: the declarations of the source (URIs) left out — they would need an extension schema with structured types; PDF/A-4
    /// keeps them (docs/ABLAUFPLAN.md, 6.1 step 7).
    /// </summary>
    public IReadOnlyList<string> DeclarationsLeftOut { get; init; } = [];

    /// <summary>The version was raised for the content (<see cref="VersionRaised"/>), not for the encryption: what needs it, e.g. „Ebenen“ (No. 553).</summary>
    public string? RaisedFor { get; init; }

    /// <summary>Saved as 2.0, but the XMP of the document could not be read: the date of the change and the program are not in it (No. 552).</summary>
    public bool XmpNotUpdated { get; init; }

    /// <summary>
    /// A tab order was changed on a page of a tagged file: the annotations are written in that order, but /Tabs /S stays — readers that
    /// follow it take the order of the structure (docs/FEHLERGEDAECHTNIS.md, No. 491). Said to the user after saving.
    /// </summary>
    public bool TabOrderByStructure { get; init; }

    /// <summary>
    /// Developer extensions (prefixes) whose declaration was removed because the version written cannot carry it (based on a later
    /// version, ISO_ below 2.0; docs/FEHLERGEDAECHTNIS.md, No. 488).
    /// </summary>
    public IReadOnlyList<string> DroppedExtensions { get; init; } = [];

    /// <summary>
    /// The document parts (/DPartRoot, /DPart; ISO 32000-2, 14.12) were removed: pages were deleted, reordered or added, and the hierarchy as
    /// read could not be followed to rebuild it for the pages written (a cycle, ranges that overlap or name no page of the file; otherwise it is
    /// rebuilt, decided on 12.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 790). Said to the user after saving.
    /// </summary>
    public bool DocumentPartsDropped { get; init; }

    /// <summary>Per page (index in the written order): the pieces by which bars grew to hide the length of redacted words (page space).</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<PdfRectangle>> BarExtensions { get; init; } = new Dictionary<int, IReadOnlyList<PdfRectangle>>();

    /// <summary>
    /// PDF/A: the fonts (by name) that were not embedded, or whose program could not be read, embedded as the substitute the display draws
    /// them with — the copy looks as in PDF-Knead, not necessarily as with the original font; said to the user.
    /// </summary>
    public IReadOnlyList<string> SubstitutedFonts { get; init; } = [];
}

/// <summary>
/// Writes a document completely anew (never incrementally, otherwise old versions, such as unredacted text, would remain
/// in the file). The pages may come from several files: each source gets its own number mapping, and only what is
/// reachable from the selected pages and the catalogue is copied (garbage of old versions is dropped in the process).
/// References to pages that are not included become null.
/// <para>
/// Catalogue, bookmarks, form and metadata come from the main document. If the page sequence is unchanged, everything stays
/// as it was; otherwise bookmarks and form fields are reduced to the remaining pages, bookmarks and fields of inserted files
/// are added (fields with the same name are renamed), and page labels and XFA are dropped.
/// </para>
/// </summary>
public static partial class PdfWriter
{
    /// <summary>Selectable versions.</summary>
    public static readonly IReadOnlyList<string> Versions = ["1.4", "1.5", "1.6", "1.7", "2.0"];

    /// <summary>Writes pages to a stream as a new PDF file.</summary>
    /// <param name="main">The main document (catalogue, bookmarks, form, metadata, encryption).</param>
    /// <param name="pages">The pages to write, in order; they may come from several documents.</param>
    /// <param name="output">The output stream; written sequentially and not disposed.</param>
    /// <param name="options">The save options; null for the defaults.</param>
    /// <param name="cancel">Cancels the writing.</param>
    /// <returns>The result.</returns>
    /// <remarks>With a <see cref="PdfSaveOptions.CompressionCache"/>, runs with the same cache are serialised.</remarks>
    public static PdfSaveResult Write(PdfDocument main, IReadOnlyList<PageItem> pages, Stream output,
        PdfSaveOptions? options = null, CancellationToken cancel = default)
    {
        options = WithCompression(options ?? new PdfSaveOptions(), main);
        if (options.PdfA != PdfAConformance.None) options = ForPdfA(options);
        var start = output.CanSeek ? output.Position : 0;
        PdfSaveResult result;
        if (options.Compression is null || options.CompressionCache is not { } cache) result = new Session(main, pages, output, options, cancel).Run();
        else
        {
            // One run at a time with the same cache (trial run and save could otherwise overlap)
            lock (cache.Gate) result = new Session(main, pages, output, options, cancel).Run();
        }
        // the second check of redacted pages, on the file as written, where it can be read back (in memory); a file is checked by
        // PdfFileSaver before it replaces the target (docs/FEHLERGEDAECHTNIS.md, No. 461)
        if (result.RedactionChecks is { } checks && output.CanRead && output.CanSeek)
        {
            var end = output.Position;
            var bytes = new byte[end - start];
            output.Position = start;
            output.ReadExactly(bytes);
            output.Position = end;
            using (var written = PdfDocument.OpenWritten(bytes, result.Security)) Redactor.CheckWritten(written, checks.Pages, checks.ContentAsRedacted, cancel);
            result = result with { RedactionChecks = null };
        }
        return result;
    }

    /// <summary>Writes a whole document anew, unchanged.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="output">The output stream.</param>
    /// <param name="options">The save options; null for the defaults.</param>
    /// <param name="cancel">Cancels the writing.</param>
    /// <returns>The result.</returns>
    public static PdfSaveResult Write(PdfDocument doc, Stream output, PdfSaveOptions? options = null, CancellationToken cancel = default) =>
        Write(doc, PageItem.From(doc), output, options, cancel);

    /// <summary>
    /// Compares versions such as "1.7" and "2.0": a version is two integers, major and minor (ISO 32000-2, I.2; 7.12.5 for BaseVersion), so
    /// „1.10“ comes after „1.9“ — before both were compared as decimal fractions (M3, Annex I; M5, 7.12.5; 23.11.2026). Texts that are no such
    /// version are compared as numbers, else ordinally.
    /// </summary>
    /// <param name="a">The first version.</param>
    /// <param name="b">The second version.</param>
    /// <returns>Negative, zero or positive, as for <see cref="IComparer{T}.Compare"/>.</returns>
    public static int CompareVersions(string a, string b)
    {
        if (VersionParts(a) is { } x && VersionParts(b) is { } y) return x.CompareTo(y);
        return double.TryParse(a, CultureInfo.InvariantCulture, out var p) && double.TryParse(b, CultureInfo.InvariantCulture, out var q)
            ? p.CompareTo(q)
            : string.CompareOrdinal(a, b);
    }

    /// <summary>Major and minor of a version „M.m“ (digits only); null for anything else.</summary>
    internal static (int Major, int Minor)? VersionParts(string version)
    {
        var dot = version.IndexOf('.');
        return dot > 0 && int.TryParse(version.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var major) &&
               int.TryParse(version.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            ? (major, minor) : null;
    }

    /// <summary>
    /// The newest of the versions of the files whose pages are saved together: a document with pages inserted from another file is saved in
    /// the more recent of the two versions by default (ISO 32000-2, I.2, sentence 17; docs/FEHLERGEDAECHTNIS.md, No. 1000). Only version numbers
    /// count — a /Version that is no number raises nothing.
    /// </summary>
    /// <param name="versions">The versions (<see cref="PdfDocument.Version"/> of each file).</param>
    /// <returns>The newest; "1.0" if none is a number.</returns>
    public static string NewestVersion(IEnumerable<string> versions) =>
        versions.Where(v => double.TryParse(v, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
            .Aggregate("1.0", (newest, v) => CompareVersions(v, newest) > 0 ? v : newest);

    /// <summary>
    /// The version a merge of whole files is written in without a chosen one (command line `zusammenfuegen`): 2.0 where one of the files is
    /// 2.0 or later — a file above 2.0 is written as 2.0, the version this writer conforms to (ISO 32000-2, 7.5.2, sentence 15; S-B B52) —,
    /// otherwise 1.7, which holds every feature of the 1.x files (I.2, sentence 17; docs/FEHLERGEDAECHTNIS.md, No. 1000).
    /// </summary>
    /// <param name="versions">The versions of the files merged.</param>
    public static string MergeVersion(IEnumerable<string> versions) => CompareVersions(NewestVersion(versions), "2.0") >= 0 ? "2.0" : "1.7";

    /// <summary>Minimum version for an encryption.</summary>
    internal static string RequiredVersion(StandardSecurityHandler security) => security.Revision >= 7 ? "2.0" : security.StreamCipher switch
    {
        PdfCipher.Aes256Gcm => "2.0",
        PdfCipher.Aes256 => "1.7",
        PdfCipher.Aes128 => "1.6",
        PdfCipher.Rc4 when security.KeyBits > 40 => "1.4",
        _ => "1.1",
    };

    private static readonly PdfName ProducerKey = PdfName.Get("Producer");
    private static readonly PdfName TitleKey = PdfName.Get("Title"), LangKey = PdfName.Get("Lang"), ViewerPreferencesKey = PdfName.Get("ViewerPreferences");

    private static PdfDictionary DictOf(params (string Key, PdfObject Value)[] entries)
    {
        var dict = new PdfDictionary(entries.Length);
        foreach (var (key, value) in entries) dict.Set(PdfName.Get(key), value);
        return dict;
    }
    private static readonly PdfName ModDateKey = PdfName.Get("ModDate");

    /// <summary>Key for the origin record of sealed signatures (JSON), see <see cref="SignatureSeal"/>.</summary>
    public static readonly PdfName SignaturesKey = PdfName.Get("PDFKneadSignatures");
    private static readonly PdfName PageLabels = PdfName.Get("PageLabels");
    private static readonly PdfName AcroForm = PdfName.Get("AcroForm");
    private static readonly PdfName Fields = PdfName.Get("Fields");
    private static readonly PdfName Xfa = PdfName.Get("XFA");
    private static readonly PdfName NeedsRendering = PdfName.Get("NeedsRendering");
    private static readonly PdfName OCProperties = PdfName.Get("OCProperties");
    private static readonly PdfName Ocgs = PdfName.Get("OCGs");
    private static readonly PdfName D = PdfName.Get("D");
    private static readonly PdfName Off = PdfName.Get("OFF");
    private static readonly PdfName Order = PdfName.Get("Order");
    private static readonly PdfName OpenAction = PdfName.Get("OpenAction");
    private static readonly PdfName Dest = PdfName.Get("Dest");
    private static readonly PdfName A = PdfName.Get("A");
    private static readonly PdfName S = PdfName.Get("S");
    private static readonly PdfName T = PdfName.Get("T");
    private static readonly PdfName Annots = PdfName.Get("Annots");
    private static readonly PdfName Dr = PdfName.Get("DR");
    private static readonly PdfName Font = PdfName.Get("Font");
    private static readonly PdfName Title = PdfName.Get("Title");
    private static readonly PdfName Next = PdfName.Get("Next");
    private static readonly PdfName PrevKey = PdfName.Get("Prev");
    private static readonly PdfName Last = PdfName.Get("Last");
    private static readonly PdfName C = PdfName.Get("C");
    private static readonly PdfName FlateDecode = PdfName.Get("FlateDecode");
    private static readonly PdfName DecodedLength = PdfName.Get("DL");
    private static readonly PdfName Da = PdfName.Get("DA");
    private static readonly PdfName XObject = PdfName.Get("XObject");

    /// <summary>A source file: what its object numbers are called in the new file.</summary>
    private sealed class Source(PdfDocument doc, bool isMain)
    {
        public PdfDocument Doc { get; } = doc;
        public bool IsMain { get; } = isMain;
        public Dictionary<int, int> Map { get; } = new(Math.Min(doc.ObjectCount, 1 << 20));
        /// <summary>Nodes and pages of the old page tree; references to them become null, except to pages that are taken over.</summary>
        public HashSet<int> TreeNodes { get; } = [];
        /// <summary>Pages taken over: old number → new number (for pages taken over twice, the first one).</summary>
        public Dictionary<int, int> PageOut { get; } = [];
        /// <summary>Pages taken over by page index in the source → new number.</summary>
        public Dictionary<int, int> PageOutByIndex { get; } = [];
        /// <summary>Replacements for individual objects (renamed form fields).</summary>
        public Dictionary<int, PdfObject> Overrides { get; } = [];
        /// <summary>Dropped objects (references to them become null): annotations and fields under redactions.</summary>
        public HashSet<int> Removed { get; } = [];
        /// <summary>Redacted pages (old numbers): replacement texts of the structure that point to them are dropped.</summary>
        public HashSet<int> RedactedPages { get; } = [];
        /// <summary>Edited text: per page (old number) the marked contents whose replacement texts in the structure are dropped.</summary>
        public Dictionary<int, HashSet<int>> EditedMcids { get; } = [];
        /// <summary>
        /// Forms rewritten because characters were removed from them (old numbers): replacement texts of structure elements whose
        /// content references (MCR with /Stm) point into them are dropped — OCR layers often lie in a form of their own.
        /// </summary>
        public HashSet<int> EditedForms { get; } = [];

        /// <summary>
        /// For a file merged into a tagged copy whose tree comes along: where its keys into the parent tree begin in the copy (its pages'
        /// /StructParents, its annotations' /StructParent and its forms' /StructParents are shifted by it; docs/ABLAUFPLAN.md, 6.5 step 3).
        /// Null: its tree does not come along, its keys go.
        /// </summary>
        public long? StructKeyOffset { get; set; }

        /// <summary>The language on the parts a merged file's tree hangs in (where it differs from the copy's); null: none (6.5 a, d).</summary>
        public string? PartLanguage { get; set; }

        /// <summary>The first part a merged file's tree hangs in (the target of its branch of bookmarks in PDF 2.0, 6.5 e); null: none.</summary>
        public PdfObject? FirstPart { get; set; }

        /// <summary>The first copy of each rewritten form with keys of its own, by the form's object number (<see cref="Session.PlanFormCopies"/>, 6.6 a).</summary>
        public Dictionary<int, PdfStream> FormCopies { get; } = [];

        /// <summary>
        /// Marked content that painted before and paints nothing in the copy (its content redacted, removed or edited away), by page object
        /// number (<see cref="Session.EmptiedMarkedContent"/>, 6.6 b).
        /// </summary>
        public Dictionary<int, HashSet<int>> EmptiedMcids { get; } = [];

        /// <summary>Structure elements already looked at: whether they (or an element below them) cover edited text.</summary>
        public Dictionary<PdfDictionary, bool> EditedCover { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Structure elements already looked at: whether they (or an element below them) have content on a redacted page.</summary>
        public Dictionary<PdfDictionary, bool> RedactedCover { get; } = new(ReferenceEqualityComparer.Instance);
        public List<PageItem> Items { get; } = [];
        public int CatalogNumber { get; init; } = -1;

        /// <summary>Number of the root of the page tree (/Pages of the catalogue), -1 if it is no object of its own.</summary>
        public int PagesRootNumber { get; init; } = -1;
        public int InfoNumber { get; init; } = -1;
        public int EncryptNumber { get; init; } = -1;
        public PdfSerializer.ReferenceMap? Mapper { get; set; }

        /// <summary>Layers of the file, evaluated once (queried per annotation when flattening; with 500 layers and
        /// 6,000 fields, evaluating them per query allocates over 200 MB).</summary>
        public Content.OptionalContent OptionalContent => field ??= Content.OptionalContent.For(Doc);

        /// <summary>Layers in their print state (<see cref="PdfSaveOptions.ForPrint"/>).</summary>
        public Content.OptionalContent PrintOptionalContent => field ??= Content.OptionalContent.For(Doc, print: true);

        /// <summary>Layers shown neither on screen nor in print: what the sanitising removes (<see cref="CleanupItems.HiddenLayers"/>).</summary>
        public Content.OptionalContent NeverShownContent => field ??= Content.OptionalContent.NeverShown(Doc);
    }

    private sealed partial class Session
    {
        private const int ObjectStreamLimit = 200;
        private const int ObjectStreamBytes = 512 * 1024;

        private readonly PdfDocument _main;
        private IReadOnlyList<PageItem> _pages;
        private readonly PdfSaveOptions _options;
        private readonly CancellationToken _cancel;
        private readonly Stream _output;
        private long _position;

        // Cross-references: index = new number; type 1 = in the file (position), 2 = in an object stream (number, index)
        private readonly List<(byte Type, long F2, int F3)> _xref = [(0, 0, 65535)];
        private readonly Dictionary<PdfDocument, Source> _sources = new(ReferenceEqualityComparer.Instance);
        private readonly List<Source> _sourceOrder = [];
        private readonly Queue<(Source Source, int Old, int New)> _queue = new();
        // Start small and grow (double) when needed: small files are the rule
        private readonly ByteBuffer _scratch = new(1024);
        private readonly MemoryStream _zip = new();
        private readonly Dictionary<PdfDictionary, int> _sharedResources = new(ReferenceEqualityComparer.Instance);

        private StandardSecurityHandler? _security;
        private PdfDictionary? _encryptDict;
        // the encryption of the file is kept (its /CF and /EFF with it): streams keep crypt filters of their own (No. 442)
        private bool _keepsFilters;
        private int _encryptNumber = -1;
        // the encryption dictionary written directly into the cross-reference stream (No. 811); null: an object of its own or none
        private PdfObject? _encryptInline;
        private string _version = "1.7";
        private bool _objectStreams;
        private int _catalogNumber;

        /// <summary>Number of the copy's page tree root (it stands for all pages, as the source's did).</summary>
        private int _pagesRootNumber;
        private int _infoNumber = -1;

        // Elements: objects in memory (without a file) by identity → new number
        private readonly Dictionary<PdfObject, int> _memoryNumbers = new(ReferenceEqualityComparer.Instance);
        private readonly Queue<(int Number, PdfObject Obj)> _memoryQueue = new();
        private readonly List<int> _newFields = [];
        private readonly HashSet<string> _fieldNames = new(StringComparer.Ordinal);

        private readonly ByteBuffer _osHeader = new(256);
        private readonly ByteBuffer _osBody = new(2048);
        private int _osNumber;
        private int _osCount;

        public Session(PdfDocument main, IReadOnlyList<PageItem> pages, Stream output, PdfSaveOptions options, CancellationToken cancel)
        {
            _main = main;
            _output = output;
            _options = options;
            _cancel = cancel;
            if (options.PdfA != PdfAConformance.None) _pdfA = new PdfAState(options.PdfA);
            // the version written, not the one asked for: settling the form decides by it (2.0 asked for, 1.7 written for a kept RC4
            // encryption: the form was settled as for 2.0 and /DR given TiRo, Cour and ZaDb; docs/FEHLERGEDAECHTNIS.md, No. 878)
            _version = WrittenVersion();
            _pages = SettleAppearances(pages);
        }

        /// <summary>
        /// The version the copy is written as (the header; the catalogue's /Version can raise it later for content below 2.0, see
        /// <see cref="WriteCatalogObject"/>): the one asked for, changed by the encryption —
        /// <list type="bullet">
        /// <item>new passwords with characters outside PDFDocEncoding work only with AES-256: then 1.7;</item>
        /// <item>a kept encryption needs its version (<see cref="RequiredVersion"/>), and the old ones (revisions 2 to 4: RC4, AES-128) are
        /// deprecated in PDF 2.0 (ISO 32000-2, 7.6.4.2, Table 20) while a new one would need the passwords: then 1.7 (No. 371).</item>
        /// </list>
        /// </summary>
        /// <summary>
        /// The encryption dictionary with the /Length of the standard handler's AES crypt filters in bytes (16 for AESV2, 32 for AESV3 and
        /// AESV4) where it stood in bits (ISO 32000-2, 7.6.6, Table 25 with pdf-issues 184; ISO/TS 32003, 5: AESV4 as AESV3); a copy, the
        /// source's dictionary unchanged (docs/FEHLERGEDAECHTNIS.md, No. 1096).
        /// </summary>
        internal static PdfDictionary CryptFilterLengthInBytes(PdfDictionary encrypt)
        {
            if (encrypt.GetName(PdfName.Get("Filter"))?.Value != "Standard" || encrypt.GetDictionary(PdfName.Get("CF")) is not { } cf) return encrypt;
            PdfDictionary? newCf = null;
            foreach (var (name, _) in cf.Entries)
            {
                if (cf.GetDictionary(name) is not { } filter) continue;
                var bytes = (filter.GetName(PdfName.Get("CFM"))?.Value, filter.GetInteger(PdfName.Length)) switch
                {
                    ("AESV2", 128) => 16,
                    ("AESV3" or "AESV4", 256) => 32,
                    _ => 0,
                };
                if (bytes == 0) continue;
                var copy = new PdfDictionary();
                foreach (var (key, value) in filter.Entries) copy.Set(key, value);
                copy.Set(PdfName.Length, PdfInteger.Of(bytes));
                if (newCf is null)
                {
                    newCf = new PdfDictionary();
                    foreach (var (key, value) in cf.Entries) newCf.Set(key, value);
                }
                newCf.Set(name, copy);
            }
            if (newCf is null) return encrypt;
            var result = new PdfDictionary();
            foreach (var (key, value) in encrypt.Entries) result.Set(key, value);
            result.Set(PdfName.Get("CF"), newCf);
            return result;
        }

        private string WrittenVersion()
        {
            var version = PdfWriter.Versions.Contains(_options.Version) ? _options.Version : "1.7";
            var encryptionSource = _options.EncryptionFrom ?? _main;
            // for recipients: 256-bit AES, from PDF 1.7 (with the extension declared, as for the standard handler)
            if (_options.Recipients is { Count: > 0 })
            {
                if (CompareVersions(version, "1.7") < 0) version = "1.7";
            }
            else if (_options.Protection is { } protection)
            {
                if (PdfProtection.CipherFor(version) != PdfCipher.Aes256 &&
                    !(StandardSecurityHandler.FitsLegacyPassword(protection.UserPassword) && StandardSecurityHandler.FitsLegacyPassword(protection.OwnerPassword)))
                    version = "1.7";
            }
            else if (_options.KeepEncryption && encryptionSource.Security is { } security && encryptionSource.EncryptDictionary is not null)
            {
                var required = RequiredVersion(security);
                if (CompareVersions(required, version) > 0) version = required;
                // revisions 2 to 4 are deprecated in 2.0 (No. 371), and revision 5 never belonged to it (ISO 32000-2, 7.6.4.2, Table 21: it shall
                // not be used) — a kept revision 5 went into a 2.0 file (docs/FEHLERGEDAECHTNIS.md, No. 1098)
                if (security.Revision <= 5 && CompareVersions(version, "2.0") >= 0) version = "1.7";
            }
            return version;
        }

        private int NewNumber()
        {
            _xref.Add(default);
            return _xref.Count - 1;
        }

        public PdfSaveResult Run()
        {
            if (_pages.Count == 0) throw new PdfException(Tr.T("Ein PDF braucht mindestens eine Seite."));

            // Version (decided before anything is written, see WrittenVersion) and encryption
            var requested = PdfWriter.Versions.Contains(_options.Version) ? _options.Version : "1.7";
            _version = WrittenVersion();
            var encryptionSource = _options.EncryptionFrom ?? _main;
            // New identifier if nothing should link the copy to the original any more (redacted, sanitised, saved as image; see
            // docs/FEHLERGEDAECHTNIS.md, No. 59); only a kept encryption up to revision 4 needs the old one, as it is part of its key
            var renewId = _options.NewFileId || _options.Cleanup != CleanupItems.None || _options.ScopedCleanup is { Items: not CleanupItems.None } ||
                          _options.ApplyRedactions && _pages.Any(p => p.Elements.Any(e => e is RedactionElement));
            var keepsOldKey = _options.Protection is null && _options.Recipients is not { Count: > 0 } && _options.KeepEncryption
                              && encryptionSource.Security is { Revision: <= 4 };
            // Kept key: exactly the identifier it was derived with (after a repair, the trailer can have a different one) — also an empty
            // one, where the file had none (docs/FEHLERGEDAECHTNIS.md, No. 89 and 345)
            _firstId = keepsOldKey && encryptionSource.Security is { } kept
                ? kept.FileId
                // a shorter one is no identifier in the sense of the standard (ISO 32000-2, Table 15: at least 16 bytes; No. 746)
                : !(renewId && !keepsOldKey) && encryptionSource.Trailer.Get(PdfName.ID) is PdfArray { Count: > 0 } fileId && fileId.Get(0) is PdfString s && s.Bytes.Length >= 16
                    ? s.Bytes
                    : RandomNumberGenerator.GetBytes(16);
            if (_options.Recipients is { Count: > 0 } recipients)
            {
                if (_options.Protection is not null) throw new PdfException(Tr.T("Passwortschutz und Verschlüsselung für Empfänger schließen sich aus."));
                (_encryptDict, _security) = StandardSecurityHandler.CreateForRecipients(recipients, _firstId);
            }
            else if (_options.Protection is { } protection)
            {
                // (passwords with characters outside PDFDocEncoding, Cyrillic, …, have already raised the version to 1.7 for AES-256)
                // AES-256: SASLprep with its prohibitions (algorithm 2.A) — for the command line and other callers as well (No. 558)
                if (PdfProtection.CipherFor(_version) == PdfCipher.Aes256
                    && (StandardSecurityHandler.SaslRefusal(protection.UserPassword) ?? StandardSecurityHandler.SaslRefusal(protection.OwnerPassword)) is { } refusal)
                    throw new PdfException(refusal);
                // New encryption: a fresh key; the first part of /ID is fixed by this
                var (dict, handler) = StandardSecurityHandler.Create(PdfProtection.CipherFor(_version), protection.UserPassword,
                    protection.OwnerPassword, protection.Permissions, _firstId);
                // /Length belongs to V 2 and 3; in 2.0 it is deprecated for V 5 (ISO 32000-2, Table 20; No. 551)
                if (Pdf20) dict.Remove(PdfName.Length);
                // the crypt filter's /Length stays in bytes in every version: the standard security handler counts it so (ISO 32000-2, 7.6.6,
                // Table 25, with the resolution of pdf-issues 184 approved by ISO: 16 for AESV2, 32 for AESV3; bits only for public-key
                // handlers) — No. 753 had written bits in 2.0 (docs/FEHLERGEDAECHTNIS.md, No. 1096)
                _security = handler;
                _encryptDict = dict;
            }
            else if (_options.KeepEncryption && encryptionSource.Security is { } security && encryptionSource.EncryptDictionary is { } encrypt)
            {
                (_encryptDict, _security) = _options.GrantAllPermissions ? security.WithAllPermissions(encrypt, _firstId) : (encrypt, security);
                // a loosened /P beside a valid /Perms (revision 5 and 6: /P is not part of the key) is not carried on: the copy writes the sealed
                // rights openly too (ISO 32000-2, 7.6.4.3.3; docs/FEHLERGEDAECHTNIS.md, No. 544)
                if (!_options.GrantAllPermissions && security.Perms == PermsState.DiffersFromP && security.SealedP is { } sealedP)
                {
                    var corrected = new PdfDictionary();
                    foreach (var (key, value) in encrypt.Entries) corrected.Set(key, value);
                    corrected.Set(PdfName.Get("P"), PdfInteger.Of(sealedP));
                    _encryptDict = corrected;
                }
                // a file asking for a MAC per revision (ISO/TS 32004): PDF Knead writes none yet — the copy no longer asks for one, its rights
                // and passwords stay exactly (not unlocked; docs/ABLAUFPLAN.md, 5.2; docs/FEHLERGEDAECHTNIS.md, No. 546)
                // with a fresh MAC where it can be written (5.5 b): revision 6 or 7, PDF 2.0 written (also where revision 7 raised it from
                // 1.7; before No. 878 the MAC was decided by the version asked for), not PDF/A, the salt there (No. 557)
                _writeMac = _options.WriteMac && !_options.GrantAllPermissions && _pdfA is null && Pdf20 && security.Revision >= 6 && security.HasFileKey
                            && SourceHadMac(encryptionSource) && encrypt.GetString(PdfName.Get("KDFSalt"))?.Bytes is { Length: 32 };
                // without the file key (only the attachments encrypted, locked) nothing can be sealed anew: /Encrypt stays as it is (No. 560)
                if (!_options.GrantAllPermissions && SourceHadMac(encryptionSource) && !_writeMac && _security.HasFileKey)
                    (_encryptDict, _security) = _security.WithoutMacRequirement(_encryptDict);
                // a crypt filter of the standard handler with its /Length in bits (128, 256 — so No. 753 wrote it in 2.0) is written in
                // bytes, as Table 25 with the resolution of pdf-issues 184 asks; the key does not depend on it (No. 1096)
                // (not for a public-key handler: its crypt filters count bits, and its dictionary stays exactly — the key depends on the
                // recipient objects, not on /ID; docs/FEHLERGEDAECHTNIS.md, No. 1117)
                if (!security.IsPublicKey) _encryptDict = CryptFilterLengthInBytes(_encryptDict);
                _keepsFilters = true;
                // (the version this encryption needs, and 1.7 instead of 2.0 for revisions 2 to 4: WrittenVersion; No. 371)
            }
            // whatever the way (kept, new or no encryption): the integrity protection of such a source is not in the copy — said after saving
            _integrityDropped = SourceHadMac(encryptionSource) && !_writeMac;
            _objectStreams = CompareVersions(_version, "1.5") >= 0 && _options.ObjectStreams;

            // the places that stay, by page as given (the passes below keep the order of the pages)
            if (_options.KeepPlaces is { Count: > 0 } keepPlaces)
                foreach (var k in keepPlaces)
                    if ((uint)k.Page < (uint)_pages.Count) (_keep.TryGetValue(k.Page, out var list) ? list : _keep[k.Page] = []).Add(k);
            if (_options.ApplyRedactions) _pages = ApplyRedactions(_pages);
            var pageCleanup = _options.Cleanup | (_options.ScopedCleanup?.Items ?? CleanupItems.None);
            if ((pageCleanup & (CleanupItems.HiddenLayers | CleanupItems.InvisibleText | CleanupItems.HiddenContent | CleanupItems.SuspiciousText)) != 0 ||
                FlattensLayers)
                _pages = RemoveHiddenContent(_pages);
            if (pageCleanup.HasFlag(CleanupItems.HiddenImageParts)) _pages = CutHiddenImageParts(_pages);
            // „Bereinigen: Struktur“: the replacement texts and MCIDs in the content go too, not only the tree (6.6 c, No. 650)
            _pages = WithoutMarkedContentDescriptions(_pages);
            if (pageCleanup.HasFlag(CleanupItems.FieldValueMismatch))
                _pages = [.. _pages.Select((p, i) => CleanupOf(i).HasFlag(CleanupItems.FieldValueMismatch) ? FieldMismatch.Redrawn(p, KeepFor(i, CleanupItems.FieldValueMismatch)) : p)];
            // the pages of the scoped items as they are written from here on (the passes above gave some of them new dictionaries)
            if (_options.ScopedCleanup is { } scoped)
                _scopedPages = new HashSet<PdfDictionary>(scoped.Pages.Where(i => (uint)i < (uint)_pages.Count).Select(i => _pages[i].Page.Dictionary),
                    ReferenceEqualityComparer.Instance);
            if (_options.TextLayers is { Count: > 0 } layers)
                foreach (var (index, layer) in layers)
                    if ((uint)index < (uint)_pages.Count)
                    {
                        (_overlays.TryGetValue(_pages[index].Page.Dictionary, out var list) ? list : _overlays[_pages[index].Page.Dictionary] = []).Add(layer);
                        _textLayerForms.Add(layer);
                    }
            if (_options.Overlays is { Count: > 0 } overlays)
                foreach (var (index, forms) in overlays)
                    if ((uint)index < (uint)_pages.Count) (_overlays.TryGetValue(_pages[index].Page.Dictionary, out var list) ? list : _overlays[_pages[index].Page.Dictionary] = []).AddRange(forms);
            if (_options.AddedAnnotations is { Count: > 0 } added)
                foreach (var (index, annotations) in added)
                    if ((uint)index < (uint)_pages.Count && annotations.Count > 0) _addedAnnotations[_pages[index].Page.Dictionary] = annotations;

            // Sources in the order of their first appearance, the main document first
            AddSource(_main);
            foreach (var item in _pages) AddSource(item.Source).Items.Add(item);
            // Edited text (see KONZEPT.md, section 5.14): replaced forms and images of the old page are included only if a page still draws them
            foreach (var item in _pages)
            {
                if (item.ReplacedObjects.Count > 0) AddReplaced(item.Source, item.ReplacedObjects);
                if (item.AlternateTextMcids.Count > 0 && item.Page.Id is { } pageId)
                {
                    var edited = AddSource(item.Source).EditedMcids;
                    if (!edited.TryGetValue(pageId.Number, out var mcids)) edited[pageId.Number] = mcids = [];
                    mcids.UnionWith(item.AlternateTextMcids);
                }
            }
            foreach (var (item, original, dropped) in _redacted) RemoveRedactedAnnotations(_sources[item.Source], item.Page.Id?.Number, original, dropped);
            // Pages replaced by an image (redacted, "additionally as image"): their annotations, field values and replacement texts
            // belong to the old content; without this they would still be included via the structure (OBJR) and calculation order (/CO)
            foreach (var item in _pages)
                if (item.Replaces is { } replaced && replaced.Source.GetObject(replaced.Id.Number) is PdfDictionary original)
                {
                    var all = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                    if (original.Get(Annots) is PdfArray annots)
                        for (var i = 0; i < annots.Count; i++)
                            if (annots.Get(i) is PdfDictionary annot) all.Add(annot);
                    RemoveRedactedAnnotations(AddSource(replaced.Source), replaced.Id.Number, original, all);
                    // What the old page drew (images, forms, patterns) is included only if a page of the new file draws it
                    if (replaced.Source.Pages.FirstOrDefault(p => p.Id == replaced.Id) is { } oldPage)
                    {
                        var drawn = new HashSet<int>();
                        ResourceUsage.PageObjects(replaced.Source, oldPage, drawn);
                        AddReplaced(replaced.Source, drawn);
                    }
                }
            // Foreign annotations: moved ones with their new position, deleted ones removed from the file entirely, with pop-up and replies
            PlanFieldLinks();   // linked fields: which widgets go together (KONZEPT.md, section 5.4a)
            foreach (var item in _pages) ForeignChanges(item);
            for (var i = 0; i < _pages.Count; i++)
                if (CleanupOf(_pages[i]).HasFlag(CleanupItems.HiddenContent)) DropUnseenAnnotations(_pages[i], KeepFor(i, CleanupItems.HiddenContent));
            ApplyFieldValues();   // filled-in fields: values into the terminal fields
            ApplyFieldLinks();
            if (pageCleanup.HasFlag(CleanupItems.InvisibleText)) RemoveInvisibleTextFromAppearances();
            // the preview in the XMP shows the first page as it was: it goes with any change of content or page order, and with „Vorschaubilder“
            // (decided on 3.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 235 and 446)
            if (!_options.Cleanup.HasFlag(CleanupItems.Metadata) && (_options.PartOfDocument || _anyRedaction || _options.Cleanup != CleanupItems.None ||
                                                                    _options.Flatten || !ContentUnchanged()))
                RemoveXmpThumbnails();
            TitleIntoXmp();
            AttachedFileDataIntoSlimXmp();
            ReconcileInfo();
            SavedIntoXmp();
            PdfAClaimOff();
            AttachedFileDataOff();
            if (_replaced.Count > 0) DropReplacedOriginals();
            // rewritten forms with keys of their own: the first copy takes the original's place in the structure (6.6 a, No. 647)
            PlanFormCopies();
            if (_touchedFonts.Count > 0) ScrubFonts();
            if (_options.Cleanup.HasFlag(CleanupItems.HiddenLayers)) DropHiddenLayers();

            // Cross-references about as large as all sources together: no repeated copying while growing
            _xref.Capacity = (int)Math.Min(1 << 22, _sourceOrder.Sum(s => (long)s.Doc.ObjectCount) + _pages.Count + 64);
            _catalogNumber = NewNumber();
            var pagesRoot = _pagesRootNumber = NewNumber();
            var pageNumbers = new int[_pages.Count];
            for (var i = 0; i < _pages.Count; i++)
            {
                pageNumbers[i] = NewNumber();
                var source = _sources[_pages[i].Source];
                if (_pages[i].Page.Id is { } id) source.PageOut.TryAdd(id.Number, pageNumbers[i]);
                source.PageOutByIndex.TryAdd(_pages[i].Page.Index, pageNumbers[i]);
                // Replacement of a page (saved as image): references to the old page point to it
                if (_pages[i].Replaces is { } replaced) AddSource(replaced.Source).PageOut.TryAdd(replaced.Id.Number, pageNumbers[i]);
            }
            // also when objects were removed (annotations a bar touched): an object reference of the structure to one of them would point at
            // nothing (docs/FEHLERGEDAECHTNIS.md, No. 363)
            _pageNumbers = pageNumbers;
            // the document parts for pages deleted, reordered, taken twice or added: built anew (No. 790)
            PlanDocumentParts(pageNumbers);
            // attachments under the name of their file (7.7.4, sentence 47; No. 909)
            PlanAttachmentRenames();
            // marked content left empty by redacting or editing: its elements go when nothing else is left in them (6.6 b, No. 649)
            EmptiedMarkedContent();
            // edited text takes the place of the marked content whose characters it took: that content's element stays (6.6 i, No. 656)
            PlanRefills();
            if ((!StructureUnchanged() || _sources[_main].Removed.Count > 0 || _sources[_main].EmptiedMcids.Count > 0) &&
                !_options.Cleanup.HasFlag(CleanupItems.Structure))
                PruneStructure();
            // the empty marked content of elements that went: artifact content, no MCID left without an element (6.6 b)
            PlanEmptiedArtifacts();
            // the trees of the files merged in, under the copy's top Document (No. 638)
            MergeStructure();
            // the pages of a merged file whose tree does not come along: their marked content without /MCID (No. 689)
            UnmarkForeignContent();
            // links on pages written whose destination page is not: without the destination (No. 692)
            LinksWithoutGoneDestinations();
            // a page taken more than once: every further copy its own structure (6.5 d, No. 642)
            DuplicatePageStructure();
            // a page replaced by its image: the image a Figure where the old page's elements went (6.6 g, No. 654)
            ImagePageFigures();
            // bars within text of a 2.0 tree: inline artifacts in the structure (6.3 step 6, No. 659)
            InlineBarArtifacts();
            // what breaks accessibility, once the structure left is known (with no tagged content left the copy is no longer tagged, No. 626)
            CollectAccessibilityBreaks();
            PdfUaClaimOff();
            // PDF/A-4 has no information dictionary (ISO 19005-4, 6.1.3; what it says goes into the XMP metadata) — unless the catalogue keeps
            // private data, whose currency the date of the last change tells (ISO 32000-2, 14.5; docs/FEHLERGEDAECHTNIS.md, No. 367). Also
            // when a PDF/A-4 file is saved the ordinary way and keeps its identification (No. 707)
            _infoNumber = (_options.PdfA.Part() == 4 || KeepsPdfA4Claim()) && !KeepsCatalogPieceInfo() ? 0 : NewNumber();
            // with a cross-reference stream the encryption dictionary stands directly in it: the model of ISO 32000-2 asks for that from PDF 2.0
            // (correction of 7.5.8.2, pdf-issues 246: an indirect one could be looked for in an encrypted object stream), the text of 7.5.8.2
            // allows both, and so does ISO 32000-1 — direct in every version, behind a table it stays an object of its own (No. 811)
            if (_security is not null && !_objectStreams) _encryptNumber = NewNumber();
            if (_pdfA is not null) PreparePdfA();
            // forms, patterns and Type 3 fonts get the resources they borrow from the page (PDF/A sees to it itself; No. 549), in every version
            // — PDF 2.0 no longer allows inheriting, below 2.0 it is deprecated and writers should not use it (ISO 32000-2, 7.8.3; ISO 32000-1,
            // 7.8.3); one drawn with same-named, different resources is written once per set that fits together, so it looks as before wherever
            // it is drawn (No. 782, 895; before, below 2.0 it stayed inheriting, in 2.0 the first set won)
            else
            {
                var ambiguous = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                var givens = new Dictionary<PdfDictionary, List<PdfDictionary>>(ReferenceEqualityComparer.Instance);
                var ownerSource = new Dictionary<PdfDictionary, PdfDocument>(ReferenceEqualityComparer.Instance);
                foreach (var item in _pages)
                {
                    _cancel.ThrowIfCancellationRequested();
                    ResourceUsage.Borrowed(item.Source, item.Page, _borrowed, ambiguous, givens);
                    foreach (var owner in givens.Keys) ownerSource.TryAdd(owner, item.Source);
                }
                // every owner: only the names it uses, one copy per set that fits (ambiguous owners are among them)
                foreach (var (owner, all) in givens)
                    if (ownerSource.TryGetValue(owner, out var doc) && _sources.TryGetValue(doc, out var ownerSrc)) PlanResourceVariants(owner, all, ownerSrc);
                    else if (ambiguous.Contains(owner)) _borrowed.Remove(owner);
            }
            // the content streams whose numbers are written as 7.3.3 allows (No. 930); PDF/A writes its content anew anyway
            if (_pdfA is null)
                foreach (var item in _pages)
                {
                    _cancel.ThrowIfCancellationRequested();
                    _contentStreams.AddPage(item.Source, item.Page);
                }
            PrepareElements();
            PrepareFonts();

            if (_writeMac) _macHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            WriteHeader();
            WritePageTree(pagesRoot, pageNumbers);
            // PDF/A: the catalogue's XMP metadata repeats the information dictionary, which is therefore settled first
            if (_pdfA is not null) WriteInfo();
            WriteCatalog(pagesRoot);
            if (_pdfA is null) WriteInfo();
            // the changes to the structure tree, once every page is written (new elements, the parent tree built anew; 6.2 step 5)
            FinishStructureEdits();
            DrainAll();
            // the catalogue last: it names the version the content written needs (No. 553)
            WriteCatalogObject();
            if (_pdfA is not null) WriteAssociated();
            if (_encryptDict is not null)
            {
                var encryptObject = Fresh(Inlined(_encryptDict, encryptionSource));
                if (_encryptNumber > 0) WriteDirect(_encryptNumber, encryptObject, null, encrypt: false);
                else _encryptInline = encryptObject;
            }
            FlushObjectStream();
            var objects = _xref.Count - 1;
            WriteXRef();
            if (_writeMac) FinishMac();
            _output.Flush();
            return new PdfSaveResult(_version, _position, objects, CompareVersions(_version, requested) > 0, _security) { PdfAClaimRemovedFor = _pdfAClaimRemovedFor, AccessibilityClaimRemoved = _accessibilityClaimRemoved, AccessibilityBreaks = [.. _accessibilityBreaks], DeclarationsLeftOut = [.. _declarationsLeftOut], RaisedFor = CompareVersions(_version, requested) > 0 && _contentVersion == _version ? _contentReason : null, IntegrityProtectionDropped = _integrityDropped, XmpNotUpdated = _xmpNotUpdated, IdleHiddenMarks = _idleHidden, LengthsPartlyShown = _partlyShown, BarExtensions = _barExtensions, DroppedExtensions = _droppedExtensions, DocumentPartsDropped = _documentPartsDropped, TabOrderByStructure = _tabOrderByStructure, ImagePartsKept = _imagePartsKept, SubstitutedFonts = [.. _substitutedNames],
                // what else changes the content of the pages on saving makes the characters outside the bars differ by intent
                RedactionChecks = _writtenChecks.Count > 0
                    ? (_writtenChecks, _options.Cleanup == CleanupItems.None && _options.ScopedCleanup is null && _pdfA is null && !_options.Flatten)
                    : null };
        }

        // ------------------------------------------------------------ Sources and numbers

        private Source AddSource(PdfDocument doc)
        {
            if (_sources.TryGetValue(doc, out var source)) return source;
            var isMain = ReferenceEquals(doc, _main);
            source = new Source(doc, isMain)
            {
                CatalogNumber = doc.Trailer.GetRaw(PdfName.Root) is PdfReference root ? root.Id.Number : -1,
                PagesRootNumber = doc.Catalog?.GetRaw(PdfName.Pages) is PdfReference pagesRoot ? pagesRoot.Id.Number : -1,
                // only if it is a dictionary: a damaged trailer pointing /Info at another object (an integer that is the length of a font
                // program) made every reference to that object point at the new information dictionary (docs/FEHLERGEDAECHTNIS.md, No. 361)
                InfoNumber = doc.Trailer.GetRaw(PdfName.Info) is PdfReference info && SafeObject(doc, info.Id.Number) is PdfDictionary ? info.Id.Number : -1,
                EncryptNumber = doc.EncryptNumber ?? -1,
            };
            var captured = source;
            source.Mapper = id => Map(captured, id.Number);
            CollectTree(doc, source.TreeNodes);
            _sources[doc] = source;
            _sourceOrder.Add(source);
            return source;
        }

        /// <summary>All object numbers in the page tree (nodes and pages).</summary>
        private static void CollectTree(PdfDocument doc, HashSet<int> numbers)
        {
            var stack = new Stack<(PdfObject Node, int Depth)>();
            if (doc.Catalog?.GetRaw(PdfName.Pages) is { } root) stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (node, depth) = stack.Pop();
                if (depth > PdfLimits.MaxPageTreeDepth) continue;
                if (node is PdfReference r)
                {
                    // only a dictionary is a node of the tree: a damaged catalogue pointing /Pages at the widths of a font left that array
                    // out of the file as an old node, and the font lost its widths (docs/FEHLERGEDAECHTNIS.md, No. 423; the same trap as /Info, No. 361)
                    var resolved = doc.Resolve(r.Id);
                    if (resolved is not PdfDictionary || !numbers.Add(r.Id.Number)) continue;
                    node = resolved;
                }
                if (node is not PdfDictionary dict || dict.GetArray(PdfName.Kids) is not { } kids) continue;
                for (var i = 0; i < kids.Count; i++) stack.Push((kids[i], depth + 1));
            }
            foreach (var page in doc.Pages)
                if (page.Id is { } id) numbers.Add(id.Number);
        }

        /// <summary>Whether the catalogue as written keeps private data of other applications (/PieceInfo).</summary>
        private bool KeepsCatalogPieceInfo() =>
            _main.Catalog is { } pieceCatalog && Present(pieceCatalog, PieceInfoKey) && !_options.Cleanup.HasFlag(CleanupItems.PrivateData);

        /// <summary>
        /// Saved the ordinary way (not as PDF/A), the source declares PDF/A-4 and keeps the identification (<see cref="PdfAClaimOff"/> did
        /// not remove it): the copy must then follow 6.1.3 as well, otherwise the kept claim would be false (No. 707).
        /// </summary>
        private bool KeepsPdfA4Claim() => _pdfA is null && _options.PdfA == PdfAConformance.None && _pdfAClaimRemovedFor is null
                                          && !_options.Cleanup.HasFlag(CleanupItems.Metadata) && PdfACheck.Declared(_main)?.Part() == 4;

        private static readonly PdfName PieceInfoKey = PdfName.Get("PieceInfo");

        /// <summary>The object of a number, or null if it cannot be read.</summary>
        private static PdfObject? SafeObject(PdfDocument doc, int number)
        {
            try
            {
                return doc.HasObject(number) ? doc.GetObject(number) : null;
            }
            catch (PdfException)
            {
                return null;
            }
        }

        /// <summary>New number of a source object; the first time, it is queued for copying. 0 = dropped.</summary>
        private int Map(Source source, int old)
        {
            if (source.PageOut.TryGetValue(old, out var page)) return page;
            if (source.Removed.Contains(old)) return 0;
            // the root of the main file's page tree stands for all pages: the copy's root (a key of another program naming it went;
            // docs/FEHLERGEDAECHTNIS.md, No. 881); the nodes below it have no counterpart in the tree written anew
            // — only where it is a node of the tree as read: a damaged catalogue pointing /Pages at the widths of a font would point the font
            // at the page tree (No. 423)
            if (source.IsMain && old == source.PagesRootNumber && source.TreeNodes.Contains(old) && _pagesRootNumber > 0) return _pagesRootNumber;
            if (source.TreeNodes.Contains(old) || old == source.EncryptNumber) return 0;
            if (old == source.CatalogNumber) return source.IsMain ? _catalogNumber : 0;
            if (source.IsMain && old == source.InfoNumber) return _infoNumber;
            if (source.Map.TryGetValue(old, out var number)) return number;
            // a reference into the void stays the void (ISO 32000-2, 7.3.10): null where it stood, not a number of its own with a null object —
            // so the copy shows it as absent as the source does, and a page inherits in its place (S-A A67; docs/FEHLERGEDAECHTNIS.md, No. 798)
            if (source.Doc.GetObject(old) is null or PdfNull) return 0;
            // Compression: the same content (from another place or file) only once
            if (Duplicate(source, old, out var key) is { } same)
            {
                source.Map[old] = same;
                return same;
            }
            number = NewNumber();
            source.Map[old] = number;
            _pdfA?.Origin.TryAdd(number, (source, old));
            RememberContent(key, number);
            _queue.Enqueue((source, old, number));
            return number;
        }

        /// <summary>
        /// Copies the direct structure and converts references into references of the new file, for objects that the writer
        /// assembles itself (they mix values from several sources).
        /// </summary>
        private PdfObject Rebase(PdfObject obj, Source source, int depth = 0)
        {
            if (depth > PdfLimits.MaxNesting) return PdfNull.Instance;
            switch (obj)
            {
                case PdfReference r:
                    var number = Map(source, r.Id.Number);
                    return number > 0 ? new OutputReference(number) : PdfNull.Instance;
                case PdfArray a:
                    var array = new PdfArray();
                    for (var i = 0; i < a.Count; i++) array.Add(Rebase(a[i], source, depth + 1));
                    return array;
                case PdfDictionary d:
                    // PDF 2.0: a standard font that is not embedded with the entries every simple font needs there (as read for PDF/A, which
                    // decides by the font as read; docs/FEHLERGEDAECHTNIS.md, No. 372)
                    var entries = Pdf20 && StandardFontEmbedding.Described(d, source.Doc) is { } described ? described : d;
                    var dict = new PdfDictionary(entries.Count);
                    foreach (var (key, value) in entries.Entries)
                        // PDF/A rebuilds the descriptors of the fonts of the file itself; a described standard font's is written here
                        dict.Set(key, key == FontDescriptorKey && value is PdfDictionary descriptor && (_pdfA is null || !ReferenceEquals(entries, d))
                            ? IndirectDescriptor((PdfDictionary)Rebase(descriptor, source, depth + 1), descriptor)
                            : Rebase(value, source, depth + 1));
                    // a Type 3 font written directly (a page's /Font dictionary is copied here) with the resources its glyphs borrow (No. 782)
                    if (_borrowed.Count > 0 && d.GetDictionary(PdfName.Resources) is null && _borrowed.TryGetValue(d, out var lent))
                        dict.Set(PdfName.Resources, LentObject(lent, source));
                    // names that stand for a copy of an owner drawn with other resources here (No. 895)
                    if (_variantNames.Count > 0 && _variantNames.TryGetValue(d, out var variantNames))
                        foreach (var (variantName, variantNumber) in variantNames) dict.Set(variantName, new OutputReference(variantNumber));
                    // PDF/A: what the copy was made from (the pages are noted by the dictionaries as read, PdfAFix)
                    if (_pdfA is not null) _pdfA.RebasedFrom[dict] = (d, source, [.. dict.Entries.Select(e => e.Value)]);
                    return dict;
                case PdfStream stream when stream.Id is { } id:
                    // Resolved stream of the source
                    var mapped = Map(source, id.Number);
                    return mapped > 0 ? new OutputReference(mapped) : PdfNull.Instance;
                case PdfStream stream:
                    return Sourced(stream, source);
                default:
                    return obj;
            }
        }

        /// <summary>Writes everything queued (which can queue new objects).</summary>
        private void Drain()
        {
            var n = 0;
            while (_queue.TryDequeue(out var entry))
            {
                if ((++n & 255) == 0) _cancel.ThrowIfCancellationRequested();
                var (source, old, number) = entry;
                var obj = source.Overrides.TryGetValue(old, out var replaced) ? replaced : source.Doc.GetObject(old);
                if (!source.IsMain && obj is PdfDictionary dict) obj = ResolveNamedDestinations(source, dict);
                // a link's structure destination to an element not written (or in a copy below 2.0) goes, its page stays (6.5 e, No. 643)
                obj = WithLiveStructureDestinations(source, obj);
                // Sanitising: keys are dropped before their targets would be queued for writing
                if (Cleanup != CleanupItems.None) obj = DocumentCleanup.Filter(obj, Cleanup, source.Doc, kept: KeptFor(source));
                // metadata in the data of images and colour profiles (EXIF, XMP boxes, device descriptions; ObjectMetadata)
                if ((_options.Cleanup & (CleanupItems.Metadata | CleanupItems.Notices)) != 0 && obj is PdfStream withData &&
                    ObjectMetadata.Clean(withData, source.Doc, _options.Cleanup) is { } cleaned)
                    obj = cleaned;
                // judged by the element as read: an element refilled with the edited text no longer names the marked content it covered (No. 676)
                if ((source.RedactedPages.Count > 0 || source.EditedMcids.Count > 0 || source.EditedForms.Count > 0) && obj is PdfDictionary element)
                    obj = WithoutAlternateText(source, element, source.Doc.GetObject(old) as PdfDictionary);
                // a marked-content reference to a rewritten form names its first copy (6.6 a, No. 647) — after the alternate texts of the
                // elements covering redacted or edited content went: that check knows the form by the original's number
                obj = WithFormCopies(source, obj);
                // a merged file whose tree comes along: its annotations and forms keep their keys, moved into its range (No. 638)
                if (source.StructKeyOffset is { } keyOffset) obj = WithStructKeysShifted(obj, keyOffset);
                // a new tree for the files merged into an untagged copy: a key the main file's objects kept would name its parts (6.5 c)
                else if (source.IsMain && _newTreePlan is not null) obj = WithStructKeysShifted(obj, null);
                // the structure tree root: its parent tree and ID tree as 7.9.6 and 7.9.7 ask (No. 779)
                if (source.IsMain && old == StructureRootNumber && obj is PdfDictionary structureRoot) obj = WithStructureTreesAsWritten(structureRoot);
                WriteObject(number, obj, source);
            }
        }

        /// <summary>
        /// Inserted files: named destinations ("Chapter2") no longer apply in the new file, so they are replaced by the
        /// destination itself.
        /// </summary>
        private static PdfObject ResolveNamedDestinations(Source source, PdfDictionary dict)
        {
            PdfDictionary? copy = null;
            if (dict.Get(Dest) is PdfName or PdfString)
            {
                copy = Clone(dict);
                if (PdfOutline.ExplicitDestination(source.Doc, dict.Get(Dest)) is { } target) copy.Set(Dest, target);
                else copy.Remove(Dest);
            }
            if (dict.Get(A) is PdfDictionary action && action.GetName(S)?.Value == "GoTo" && action.Get(D) is PdfName or PdfString)
            {
                copy ??= Clone(dict);
                if (PdfOutline.ExplicitDestination(source.Doc, action.Get(D)) is { } target)
                {
                    var newAction = Clone(action);
                    newAction.Set(D, target);
                    copy.Set(A, newAction);
                }
                else
                {
                    copy.Remove(A);
                }
            }
            return copy ?? dict;
        }

        private static PdfDictionary Clone(PdfDictionary dict)
        {
            var copy = new PdfDictionary(dict.Count);
            foreach (var (key, value) in dict.Entries) copy.Set(key, value);
            return copy;
        }

        // ------------------------------------------------------------ Pages

        private void WritePageTree(int root, int[] pageNumbers)
        {
            var parents = new int[_pages.Count];
            WriteNode(root, 0, 0, _pages.Count, pageNumbers, parents);
            for (var i = 0; i < _pages.Count; i++)
            {
                if ((i & 63) == 0) _cancel.ThrowIfCancellationRequested();
                WriteObject(pageNumbers[i], PageDictionary(_pages[i], parents[i], pageNumbers[i]), null);
            }
        }

        /// <summary>Balanced page tree with at most 64 children per node (large documents stay fast).</summary>
        private void WriteNode(int number, int parent, int start, int end, int[] pageNumbers, int[] parents)
        {
            const int fanOut = 64;
            var kids = new PdfArray();
            var count = end - start;
            if (count <= fanOut)
            {
                for (var i = start; i < end; i++)
                {
                    kids.Add(new OutputReference(pageNumbers[i]));
                    parents[i] = number;
                }
            }
            else
            {
                var chunk = Math.Max(fanOut, (count + fanOut - 1) / fanOut);
                for (var s = start; s < end; s += chunk)
                {
                    var child = NewNumber();
                    kids.Add(new OutputReference(child));
                    WriteNode(child, number, s, Math.Min(end, s + chunk), pageNumbers, parents);
                }
            }
            var node = new PdfDictionary();
            node.Set(PdfName.Type, PdfName.Pages);
            if (parent > 0) node.Set(PdfName.Parent, new OutputReference(parent));
            node.Set(PdfName.Kids, kids);
            node.Set(PdfName.Count, PdfInteger.Of(count));
            WriteObject(number, node, null);
        }

        private static readonly PdfName[] Inherited = [PdfName.Resources, PdfName.MediaBox, PdfName.CropBox];

        /// <summary>The categories of resources content names its resources by (ISO 32000-2, 7.8.3, Table 34; /ProcSet names none).</summary>
        private static readonly PdfName[] NamedCategories = [PdfName.Get("Font"), PdfName.Get("XObject"), PdfName.Get("ExtGState"),
            PdfName.Get("ColorSpace"), PdfName.Get("Pattern"), PdfName.Get("Shading"), PdfName.Get("Properties")];

        // pages that keep the same names of the same resources: one object (resources as read → the names kept → its number)
        private readonly Dictionary<PdfDictionary, Dictionary<string, int>> _usedResources = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// The resources of a page with only what its content uses (ISO 32000-2, 7.8.3, sentences 13 and 15; <see cref="ResourceUsage.UsedNames"/>,
        /// which follows what draws with the page's resources for want of its own): a category's names nothing invokes go, a category left
        /// empty goes; the default colour spaces (/DefaultGray, /DefaultRGB, /DefaultCMYK) stay — device colours use them without naming them —
        /// as do /ProcSet and categories of other programs. Pages keeping the same of the same resources share one object. Null if nothing goes
        /// or the use cannot be told for certain (then every resource stays) (docs/FEHLERGEDAECHTNIS.md, No. 788).
        /// </summary>
        private OutputReference? UsedResources(PageItem item, PdfDictionary original, Source source)
        {
            var resources = original.Get(PdfName.Resources) as PdfDictionary ?? InheritedValue(original, PdfName.Resources) as PdfDictionary;
            if (resources is null || ResourceUsage.UsedNames(item.Source, item.Page) is not { } used) return null;
            PdfDictionary? kept = null;
            var signature = new System.Text.StringBuilder();
            foreach (var category in NamedCategories)
            {
                if (resources.Get(category) is not PdfDictionary entries) continue;
                var names = used.GetValueOrDefault(category);
                bool Keep(PdfName name) => names?.Contains(name) == true ||
                                           category.Value == "ColorSpace" && name.Value is "DefaultGray" or "DefaultRGB" or "DefaultCMYK";
                var only = new PdfDictionary();
                foreach (var (name, value) in entries.Entries)
                    if (Keep(name))
                    {
                        only.Set(name, value);
                        // each name with its length: a name may hold a space or a solidus (#20, #2F) — „a Font/b“ made the key of „a“ and „b“
                        // (docs/FEHLERGEDAECHTNIS.md, No. 904)
                        signature.Append(category.Value).Append('/').Append(name.Value.Length).Append(':').Append(name.Value);
                    }
                if (only.Count == entries.Count) continue;
                // the names that stand for a copy of an owner drawn with these resources (No. 895) go along to the dictionary that keeps them:
                // they hang on the dictionary as read, and the kept one lost them — a page with unused fonts drew a Type 3 font with the first
                // page's resources again, and a glyph missed its font (test file pr4922; docs/FEHLERGEDAECHTNIS.md, No. 915)
                if (_variantNames.TryGetValue(entries, out var variants) && variants.Where(v => only.ContainsKey(v.Key)).ToDictionary() is { Count: > 0 } along)
                    _variantNames[only] = along;
                kept ??= CloneWithResolver(resources);
                if (only.Count > 0) kept.Set(category, only);
                else kept.Remove(category);
            }
            if (kept is null) return null;
            if (!_usedResources.TryGetValue(resources, out var bySignature)) _usedResources[resources] = bySignature = [];
            if (!bySignature.TryGetValue(signature.ToString(), out var number))
            {
                bySignature[signature.ToString()] = number = NewNumber();
                WriteObject(number, Rebase(kept, source), null);
            }
            return new OutputReference(number);
        }

        private static readonly PdfName StructParentKey = PdfName.Get("StructParent");
        private static readonly PdfName DPartKey = PdfName.Get("DPart"), DPartRootKey = PdfName.Get("DPartRoot");

        /// <summary>The annotations of a page from another file lose their /StructParent (see <see cref="PageDictionary"/>).</summary>
        private static void WithoutStructureKeys(Source source, PdfDictionary page)
        {
            if (page.Get(Annots) is not PdfArray annots) return;
            for (var i = 0; i < annots.Count && i < 10_000; i++)
            {
                if (annots[i] is not PdfReference reference) continue;
                var current = source.Overrides.TryGetValue(reference.Id.Number, out var o) ? o : source.Doc.Resolve(reference.Id);
                if (current is not PdfDictionary annot || !annot.ContainsKey(StructParentKey)) continue;
                var copy = Clone(annot);
                copy.Remove(StructParentKey);
                source.Overrides[reference.Id.Number] = copy;
            }
        }

        private PdfDictionary PageDictionary(PageItem item, int parent, int self)
        {
            var source = _sources[item.Source];
            var page = item.Page;
            var original = (PdfDictionary)DocumentCleanup.Filter(page.Dictionary, Cleanup, item.Source, kept: KeptFor(item.Source));
            var dict = new PdfDictionary(original.Count + 4);
            // With elements, AddElements assembles content, resources and annotations. Own annotations from an earlier
            // save are contained in item.Elements and are written anew; the old ones are dropped.
            var owned = DroppedAnnotations(item);
            var own = OwnElements(item);
            var flattenAnnots = (_options.Flatten || BannedAnnotations(page.Dictionary)) && original.Get(Annots) is PdfArray { Count: > 0 };
            var layer = _overlays.ContainsKey(page.Dictionary);
            var added = _addedAnnotations.ContainsKey(page.Dictionary);
            var flat = flattenAnnots || own.Any(e => !Annotated(e)) || layer;
            // Foreign annotations moved, deleted or put in another order: assemble /Annots anew (a new order alone was lost before:
            // docs/FEHLERGEDAECHTNIS.md, No. 491)
            var foreignChanged = ForeignChanged(item) || Reordered(item);
            var fields = flattenAnnots || owned.Count > 0 || own.Any(Annotated) || foreignChanged || added;
            // A page of another file: its key into that file's parent tree means nothing here — only the main file's structure tree is
            // written, and the key would hand its content to a stranger's element (ISO 32000-2, 14.7.5.4; docs/FEHLERGEDAECHTNIS.md, No. 435)
            if (!source.IsMain && source.StructKeyOffset is null) WithoutStructureKeys(source, original);
            foreach (var (key, value) in original.Entries)
            {
                if (key == PdfName.Parent) continue;
                if (key == ContentElements.LegacyPageKey) continue;   // the earlier name of the marker goes; written anew where blocks are (No. 599)
                // /DPart only within the range of a document part (ISO 32000-2, 7.7.3.3, Table 31): a page of another file (its parts do not
                // come along) or of a file without /DPartRoot loses it (docs/FEHLERGEDAECHTNIS.md, No. 790)
                if (key == DPartKey && (_parts is not null || !source.IsMain || _main.Catalog?.Get(DPartRootKey) is not PdfDictionary || !StructureUnchanged())) continue;
                if ((!source.IsMain || _newTreePlan is not null) && key == StructParents)
                {
                    // a merged file's key, moved into its range when its tree comes along (No. 638); else it goes (No. 435) — and so does a key
                    // the untagged main file kept, where a new tree is made for the files merged into it (6.5 c)
                    if (source.StructKeyOffset is { } offset && NumberTree.KeyOf(original.Get(StructParents)) is { } ownKey) dict.Set(StructParents, PdfInteger.Of(ownKey + offset));
                    continue;
                }
                if (flat && (key == PdfName.Contents || key == PdfName.Resources)) continue;
                if (fields && key == Annots) continue;
                // PDF/A: content written anew as one stream (PdfAContent) — the parts as read are not written
                if (_pdfA is not null && key == PdfName.Contents && (value is PdfArray || page.Dictionary.Get(PdfName.Contents) is PdfArray ||
                    page.Dictionary.Get(PdfName.Contents) is PdfStream single && _pdfA.Usage.Deep.Contains(single))) continue;
                dict.Set(key, Rebase(value, source));
            }
            if (own.Count > 0 || flattenAnnots || owned.Count > 0 || foreignChanged || layer || added) AddElements(dict, item, source, self);
            // the leaf of the document parts built anew whose range holds this page (14.12.3; No. 790)
            if (_parts is { } partsPlan && partsPlan.LeafOfPage.TryGetValue(self, out var leaf)) dict.Set(DPartKey, new OutputReference(leaf));
            // a page that had no key into the parent tree and got tagged content through the plan (docs/ABLAUFPLAN.md, 6.2 step 4)
            if ((source.IsMain || _imagePageMarks.ContainsKey(self)) && (_structurePlan ?? _newTreePlan)?.NewPageKey(self) is { } newKey) dict.Set(StructParents, PdfInteger.Of(newKey));
            // a page replaced by its image: its content the Figure's marked content (6.6 g, No. 654)
            MarkImagePage(dict, self);
            // a further copy of a page with a structure of its own (DuplicatePageStructure, No. 642)
            if (_copyPageKeys.TryGetValue(self, out var copyKey)) dict.Set(StructParents, PdfInteger.Of(copyKey));
            // Inherited values are set on the page itself (the old page tree is not copied)
            foreach (var key in Inherited)
            {
                if (Present(dict, key) || Present(original, key) || InheritedValue(original, key) is not { } value) continue;
                // Resources set directly on the /Pages node become one separate object; otherwise they would appear
                // 1,000 times in the file for 1,000 pages
                if (key == PdfName.Resources && value is PdfDictionary shared)
                {
                    if (!_sharedResources.TryGetValue(shared, out var number))
                    {
                        _sharedResources[shared] = number = NewNumber();
                        WriteObject(number, Rebase(shared, source), null);
                    }
                    dict.Set(key, new OutputReference(number));
                    continue;
                }
                dict.Set(key, Rebase(value, source));
            }
            // 7.8.3 sentences 13 and 15: the page names only the resources its content uses (decided on 12.11.2026) — not where the
            // writer writes the content anew itself (flattened, overlaid, PDF/A: those resources are its own) (docs/FEHLERGEDAECHTNIS.md, No. 788)
            // an inline image the display reads only with a tolerance (JPEG 2000 or JBIG2 inline, ASCII85 without its end, „EI“ in the data):
            // an image XObject in the page's resources, the content otherwise byte for byte (decided on 20.11.2026; S-A A12, A13,
            // A86, A110; docs/FEHLERGEDAECHTNIS.md, No. 993) — its resources keep only what is used, as below
            if (!flat && _pdfA is null && PageInlineImages(item, original, source) is var (inlineContents, inlineResources))
            {
                dict.Set(PdfName.Contents, inlineContents);
                dict.Set(PdfName.Resources, inlineResources);
            }
            else if (!flat && _pdfA is null && UsedResources(item, original, source) is { } usedOnly) dict.Set(PdfName.Resources, usedOnly);
            if (!Present(dict, PdfName.MediaBox))
            {
                var box = page.MediaBox;
                dict.Set(PdfName.MediaBox, new PdfArray([new PdfReal(box.Left), new PdfReal(box.Bottom), new PdfReal(box.Right), new PdfReal(box.Top)]));
            }
            if (_pdfA is not null)
            {
                // the content as one stream written anew (its parts may split an operator from its operands; content nested too deep
                // moves into forms in the page's resources)
                if (!flat && PdfAContent(original, page.Resources, source, out var nested) is { } content)
                {
                    dict.Set(PdfName.Contents, content);
                    if (nested is not null) dict.Set(PdfName.Resources, nested);
                }
                // a page larger than PDF/A allows: a larger unit, the page shrunk by as much (PDF/A-2 on)
                ShrinkLargePage(dict, page, source);
                // level a: the page's key in the parent tree of a structure made for it (an old key without a structure goes)
                if (_pdfA.Tags is not null)
                {
                    if (_pdfA.PageKeys.TryGetValue(page.Dictionary, out var structKey)) dict.Set(PdfName.Get("StructParents"), PdfInteger.Of(structKey));
                    else dict.Remove(PdfName.Get("StructParents"));
                }
                PdfABoxes(dict, page, source);
                PdfAMissingFonts(dict, original, source);
                // PDF/A-4: a page group whose ICC-based CMYK space a colour on the page uses as well loses its space (see PdfADictionary)
                if (_pdfA.Part.Part() == 4 && original.GetDictionary(PdfName.Get("Group")) is { } group && _pdfA.Usage.DeviceGroups.Contains(group) &&
                    dict.Get(PdfName.Get("Group")) is PdfDictionary written)
                {
                    var changed = Clone(written);
                    changed.Remove(PdfName.Get("CS"));
                    dict.Set(PdfName.Get("Group"), changed);
                }
                // PDF/A-1 flattened: no transparency group (see PdfADictionary)
                if (_pdfA.Part.Part() == 1 && _options.PdfAFlattened && original.GetDictionary(PdfName.Get("Group"))?.GetName(PdfName.Get("S"))?.Value == "Transparency")
                    dict.Remove(PdfName.Get("Group"));
            }
            if (!Present(dict, PdfName.Resources)) dict.Set(PdfName.Resources, new PdfDictionary());
            if (page.Rotate != 0) dict.Set(PdfName.Rotate, PdfInteger.Of(page.Rotate));
            else dict.Remove(PdfName.Rotate);
            AsTheStandardWantsIt(dict);
            dict.Set(PdfName.Type, PdfName.Page);
            dict.Set(PdfName.Parent, new OutputReference(parent));
            return dict;
        }

        /// <summary>
        /// What ISO 32000-2, 7.7.3.3 (Table 31) asks of a page object as written, whatever the source had (docs/FEHLERGEDAECHTNIS.md, No. 755):
        /// no empty /Contents array (without content the entry goes — the page is empty all the same); /Annots holds indirect references (a
        /// direct annotation is written as an object of its own); /LastModified wherever /PieceInfo stands (the time of writing).
        /// </summary>
        private void AsTheStandardWantsIt(PdfDictionary dict)
        {
            if (dict.GetRaw(PdfName.Contents) is PdfArray { Count: 0 }) dict.Remove(PdfName.Contents);
            if (dict.GetRaw(Annots) is PdfArray annots && annots.Any(a => a is PdfDictionary))
            {
                var indirect = new PdfArray();
                foreach (var item in annots)   // the items as they stand (references not followed)
                {
                    if (item is not PdfDictionary annot)
                    {
                        indirect.Add(item);
                        continue;
                    }
                    var number = NewNumber();
                    WriteObject(number, annot, null);
                    indirect.Add(new OutputReference(number));
                }
                dict.Set(Annots, indirect);
            }
            if (Present(dict, PieceInfoKey) && !Present(dict, LastModifiedKey))
                dict.Set(LastModifiedKey, new PdfString(System.Text.Encoding.ASCII.GetBytes(PdfDate(Modified))));
        }

        private static readonly PdfName LastModifiedKey = PdfName.Get("LastModified");

        /// <summary>
        /// Whether a dictionary has an entry for the key: an entry whose reference points into the void counts as absent (ISO 32000-2, 7.3.10;
        /// S-A A67; docs/FEHLERGEDAECHTNIS.md, No. 798) — in a dictionary of a source (the reference resolved) and in the writer's copy, where
        /// <see cref="Rebase"/> made such a reference null.
        /// </summary>
        private static bool Present(PdfDictionary dict, PdfName key) =>
            dict.GetRaw(key) is { } raw && raw is not PdfNull && (raw is not PdfReference || dict.Get(key) is not PdfNull);

        private static PdfObject? InheritedValue(PdfDictionary page, PdfName key)
        {
            var node = page.GetDictionary(PdfName.Parent);
            for (var depth = 0; node is not null && depth < PdfLimits.MaxPageTreeDepth; depth++)
            {
                if (Present(node, key)) return node.GetRaw(key);   // a node naming the void passes the value of its ancestor on (No. 798)
                node = node.GetDictionary(PdfName.Parent);
            }
            return null;
        }

        /// <summary>
        /// The pages of the main document as they are: the same pages in the same order, with the same dictionary and rotation, no edits of
        /// text or images, no elements except unchanged annotations of the file. Elements stored by PDF-Knead itself count as changes (they
        /// cannot be told from new ones here), which only removes a preview more often.
        /// </summary>
        private bool ContentUnchanged()
        {
            if (!StructureUnchanged()) return false;
            for (var i = 0; i < _pages.Count; i++)
            {
                var item = _pages[i];
                var original = _main.Pages[i];
                if (!ReferenceEquals(item.Page.Dictionary, original.Dictionary) || item.Page.Rotate != original.Rotate || item.ObjectEdits is not null ||
                    item.Replaces is not null || item.ReplacedObjects.Count > 0 || item.Elements.Any(e => e is not ForeignAnnotation { IsChanged: false }))
                    return false;
            }
            return true;
        }

        /// <summary>The same pages in the same order as in the main document (rotating does not change the structure).</summary>
        private bool StructureUnchanged()
        {
            if (_pages.Count != _main.PageCount) return false;
            for (var i = 0; i < _pages.Count; i++)
                if (!ReferenceEquals(_pages[i].Source, _main) || !SamePage(_pages[i].Page, _main.Pages[i]))
                    return false;
            return true;
        }

        /// <summary>
        /// The same page of the file: same object number. A redacted or sanitised page has a new dictionary but is the same
        /// page; comparing dictionaries would drop page labels and bookmarks afterwards and assemble the form anew.
        /// </summary>
        private static bool SamePage(PdfPage page, PdfPage original) =>
            page.Id is { } id && original.Id is { } originalId ? id == originalId : ReferenceEquals(page.Dictionary, original.Dictionary);

        // ------------------------------------------------------------ Catalogue

        private static readonly PdfName PermsKey = PdfName.Get("Perms");

        /// <summary>The copy is encrypted with AES-GCM in some filter (strings, streams or one named by /CF).</summary>
        private bool UsesGcm => _security is { } s && (s.StreamCipher == PdfCipher.Aes256Gcm || s.StringCipher == PdfCipher.Aes256Gcm || s.EmbeddedFileCipher == PdfCipher.Aes256Gcm);

        // the source asked for a MAC or had one (/AuthCode in its trailer, /KDFSalt in /Encrypt)
        private bool _integrityDropped;

        private static bool SourceHadMac(Document.PdfDocument source) =>
            source.Security is { } s && (StandardSecurityHandler.RequiresMac(s.RawPermissions, s.Revision)
                                         || source.EncryptDictionary?.HasValue(PdfName.Get("KDFSalt")) == true
                                         || source.Trailer.HasValue(PdfName.Get("AuthCode")));

        private void WriteCatalog(int pagesRoot)
        {
            var main = _sources[_main];
            var unchanged = StructureUnchanged();
            var catalog = new PdfDictionary();
            PdfObject? sourceExtensions = null;
            if (_main.Catalog is { } catalogSource && DocumentCleanup.Filter(catalogSource, Cleanup, _main, kept: _options.KeptAttachments) is PdfDictionary original)
            {
                foreach (var (key, value) in original.Entries)
                {
                    if (key == PdfName.Pages || key == PdfName.Version || key == OCProperties) continue;
                    // /Perms (/DocMDP, /UR3) names signatures over bytes the written file no longer has: dropped when the file is written
                    // anew (ISO 32000-2, 12.8.2.3 and 12.8.4, Table 263 with EC3 #218; docs/FEHLERGEDAECHTNIS.md, No. 539); appending keeps it
                    if (key == PermsKey) continue;
                    // the extensions declared: resolved and checked against the version written (below)
                    if (key == ExtensionDeclarations.Key)
                    {
                        sourceExtensions = original.Get(key);
                        continue;
                    }
                    // a part of the document: what belongs to the whole stays behind (No. 447)
                    if (_options.PartOfDocument && (key == PdfName.Get("PieceInfo") || key == PdfName.Get("AF") && !_options.KeepDocumentAttachments)) continue;
                    // without the attachments of the whole their portfolio view goes too — its /D would name a file that is gone (No. 1104)
                    if (_options.PartOfDocument && !_options.KeepDocumentAttachments && key == PdfName.Get("Collection")) continue;
                    if (_options.PartOfDocument && !_options.KeepDocumentAttachments && key == PdfName.Get("Names") && original.Get(key) is PdfDictionary names &&
                        names.ContainsKey(PdfName.Get("EmbeddedFiles")))
                    {
                        var without = new PdfDictionary(names.Count);
                        foreach (var (nameKey, nameValue) in names.Entries)
                            if (nameKey.Value != "EmbeddedFiles" && NameTreeWritten(nameKey, nameValue) is { } written) without.Set(nameKey, written);
                        if (without.Count > 0) catalog.Set(key, without);
                        continue;
                    }
                    // named destinations whose page is not written go (ISO 32000-2, 12.3.2.2: a destination names a page; No. 692) — before
                    // they were written as [null /Fit]
                    if (!unchanged && key == PdfName.Get("Names") && original.Get(key) is PdfDictionary namesWithDests &&
                        namesWithDests.Get(DestsKey) is PdfDictionary destsTree)
                    {
                        var budget = 200_000;
                        var (prunedDests, noDests) = PruneTree(destsTree, NamesKey, (_, value) => DestinationGone(main, value), ref budget, 0);
                        if (prunedDests is not null || noDests)
                        {
                            var namesCopy = new PdfDictionary(namesWithDests.Count);
                            foreach (var (nameKey, nameValue) in namesWithDests.Entries)
                                if (nameKey != DestsKey) { if (NameTreeWritten(nameKey, nameValue) is { } written) namesCopy.Set(nameKey, written); }
                                else if (!noDests && NameTreeWritten(nameKey, prunedDests!) is { } writtenDests) namesCopy.Set(nameKey, writtenDests);
                            if (namesCopy.Count > 0) catalog.Set(key, namesCopy);
                            continue;
                        }
                    }
                    if (!unchanged && key == DestsKey && original.Get(key) is PdfDictionary oldDests &&
                        oldDests.Entries.Take(100_000).Any(e => DestinationGone(main, e.Value)))
                    {
                        var kept = new PdfDictionary(oldDests.Count);
                        foreach (var (destName, destValue) in oldDests.Entries)
                            if (!DestinationGone(main, destValue)) kept.Set(destName, Rebase(destValue, main));
                        if (kept.Count > 0) catalog.Set(key, kept);
                        continue;
                    }
                    // the document parts: their ranges (/Start, /End) name pages — with pages deleted, reordered or added the hierarchy is rebuilt
                    // for the pages written (PlanDocumentParts, decided on 12.11.2026); it goes only where the one as read cannot be
                    // followed (No. 790)
                    if (!unchanged && key == DPartRootKey)
                    {
                        if (_parts is { } parts) catalog.Set(key, new OutputReference(parts.Root));
                        else _documentPartsDropped = true;   // the hierarchy as read cannot be followed
                        continue;
                    }
                    if (!unchanged && (key == PageLabels || key == PdfName.Outlines ||
                                       key == AcroForm || key == NeedsRendering))
                        continue;
                    // Flattening: no form any more
                    if (_options.Flatten && (key == AcroForm || key == NeedsRendering)) continue;
                    // New fields: the form is assembled anew below
                    if (RewriteForm && (key == AcroForm || key == NeedsRendering)) continue;
                    // Redacted: the XFA data of a form go (docs/RECHERCHE-VERSTECKEN.md, B-7) — a second copy of the field values beside the
                    // fields, often holding just what was redacted, and some readers show the form from it instead of the redacted page
                    if (_anyRedaction && key == NeedsRendering) continue;
                    if (_anyRedaction && key == AcroForm && original.Get(AcroForm) is PdfDictionary withXfa && withXfa.ContainsKey(Xfa))
                    {
                        var withoutXfa = new PdfDictionary(withXfa.Count);
                        foreach (var (formKey, formValue) in withXfa.Entries)
                            if (formKey != Xfa) withoutXfa.Set(formKey, Rebase(formValue, main));
                        catalog.Set(key, withoutXfa);
                        continue;
                    }
                    // name trees, page labels and a structure tree root given directly as 7.9.6 and 7.9.7 ask: a tree the copy cannot write as it
                    // stands is built anew (docs/FEHLERGEDAECHTNIS.md, No. 779); the root as an object of its own: when it is written (Drain)
                    if (key == NamesKey && original.Get(key) is PdfDictionary plainNames && NamesWritten(plainNames) is { } namesInOrder)
                    {
                        if (namesInOrder.Count > 0) catalog.Set(key, namesInOrder);
                        continue;
                    }
                    if (key == PageLabels && original.Get(key) is PdfDictionary labelTree && !PageLabelsAsWritten(labelTree) &&
                        RebuiltPageLabels(labelTree) is { } labelsInOrder)
                    {
                        catalog.Set(key, labelsInOrder);
                        continue;
                    }
                    catalog.Set(key, Rebase(key == StructTreeRoot && value is PdfDictionary directRoot ? WithStructureTreesAsWritten(directRoot) : value, main));
                }
            }
            catalog.Set(PdfName.Type, PdfName.Catalog);
            catalog.Set(PdfName.Pages, new OutputReference(pagesRoot));
            // The extensions declared (docs/FEHLERGEDAECHTNIS.md, No. 488): from the catalog as read, resolved — the copy above holds a
            // reference where the file had one, and the declaration built on it lost every entry but /ADBE —, without what the version
            // written cannot declare, written directly into the catalog (ISO 32000-2, 7.12.1). AES-256 in PDF 1.7: Adobe extension level 8
            // (in 2.0 it is part of the standard), raised only: a higher level stays.
            var extensions = ExtensionDeclarations.Resolved(sourceExtensions) ?? new PdfDictionary();
            _droppedExtensions = ExtensionDeclarations.DropAbove(extensions, _version);
            // ISO/TS 32004 (MAC) is not written (yet): its declaration goes, also in a PDF/A copy (No. 546)
            if (_writeMac) ExtensionDeclarations.Raise(extensions, "ISO_", "2.0", 32004, _version, "https://www.iso.org/standard/45877.html", ":2024");
            else ExtensionDeclarations.Remove(extensions, "ISO_", 32004);
            if (_security?.StreamCipher == PdfCipher.Aes256 && _version == "1.7") ExtensionDeclarations.Raise(extensions, "ADBE", "1.7", 8, _version);
            // AES-GCM (ISO/TS 32003, chapter 4): declared where the copy keeps it, gone where the encryption went or changed (No. 555)
            if (UsesGcm) ExtensionDeclarations.Raise(extensions, "ISO_", "2.0", 32003, _version, "https://www.iso.org/standard/45876.html", ":2023");
            else ExtensionDeclarations.Remove(extensions, "ISO_", 32003);
            if (ExtensionDeclarations.Any(extensions)) catalog.Set(ExtensionDeclarations.Key, Rebase(extensions, main));

            if (!unchanged)
            {
                if (!_options.Cleanup.HasFlag(CleanupItems.Bookmarks) && WriteOutlines() is { } outlines) catalog.Set(PdfName.Outlines, new OutputReference(outlines));
                // the page labels follow their pages (docs/FEHLERGEDAECHTNIS.md, No. 494) — written only if a source had some
                if (LabelsOfPages() is { } labels) catalog.Set(PageLabels, OwnPageLabels(labels));
                if (!_options.Flatten && BuildAcroForm() is { } form) catalog.Set(AcroForm, form);
            }
            FinishFieldLinks();   // the new fields of linked groups, now that their widgets are written
            if (RewriteForm)
                catalog.Set(AcroForm, WithNewFields(unchanged ? MainAcroForm() : catalog.Get(AcroForm) as PdfDictionary ?? new PdfDictionary()));
            if (_settled && catalog.Get(AcroForm) is PdfDictionary settledForm) settledForm.Remove(NeedAppearances);
            if (!FlattensLayers && OptionalContent() is { } oc) catalog.Set(OCProperties, oc);
            // Marker for editable elements: only if there are any in the file
            if (_ownElementsWritten) catalog.Set(ElementStore.CatalogKey, PdfBoolean.True);
            else catalog.Remove(ElementStore.CatalogKey);
            catalog.Remove(ElementStore.LegacyCatalogKey);   // the earlier name goes (ISO 32000-2, Annex E.2; No. 599)
            // origin records of signatures live in the information dictionary (WriteInfo); only a PDF/A-4 copy puts them here
            catalog.Remove(SignaturesKey);

            // Title, language and showing the title (docs/FEHLERGEDAECHTNIS.md, No. 492): a file without XMP gets a slim packet with the
            // title; /Lang as a valid language tag; /ViewerPreferences merged with what the file has, written directly
            if (_slimXmp is { } slim)
            {
                var number = NewNumber();
                WriteObject(number, new PdfStream(DictOf(("Type", PdfName.Get("Metadata")), ("Subtype", PdfName.Get("XML"))), slim), null);
                catalog.Set(PdfName.Get("Metadata"), new OutputReference(number));
            }
            if (_options.DocumentLanguage is { } language)
            {
                if (PdfATagging.CleanLanguage(language, 2) is { } tag) catalog.Set(LangKey, new PdfString(PdfTextEncoding.Encode(tag)));
                else if (language.Trim().Length == 0) catalog.Remove(LangKey);
            }
            if ((_options.DisplayDocTitle ?? InheritedDisplayDocTitle()) is { } showTitle)
            {
                var preferences = new PdfDictionary();
                if (_main.Catalog?.GetDictionary(ViewerPreferencesKey) is { } existingPreferences)
                    foreach (var (key, value) in existingPreferences.Entries) preferences.Set(key, Rebase(value, main));
                preferences.Set(PdfName.Get("DisplayDocTitle"), showTitle ? PdfBoolean.True : PdfBoolean.False);
                catalog.Set(ViewerPreferencesKey, preferences);
            }
            // a new file of tagged files (6.5 c): the language of the first, and the tree made for them
            if (_options.DocumentLanguage is null && !Present(catalog, LangKey) && InheritedLanguage() is { } inherited)
                catalog.Set(LangKey, new PdfString(PdfTextEncoding.Encode(inherited)));
            if (_newTreeRoot is { } newRoot)
            {
                catalog.Set(StructTreeRoot, new OutputReference(newRoot));
                catalog.Set(PdfName.Get("MarkInfo"), DictOf(("Marked", PdfBoolean.True)));
            }

            // Initial view on a page that no longer exists: leave it out
            if (catalog.Get(OpenAction) is PdfArray { Count: > 0 } open && open[0] is PdfNull) catalog.Remove(OpenAction);
            if (_pdfA is not null) catalog = PdfACatalog(catalog);
            if (Present(catalog, OCProperties)) Needs("1.5", Tr.T("Ebenen"));
            _catalog = catalog;
        }

        /// <summary>The catalogue as built by <see cref="WriteCatalog"/>, written after everything else (<see cref="WriteCatalogObject"/>).</summary>
        private PdfDictionary? _catalog;

        /// <summary>
        /// Writes the catalogue once all other objects are written: where they need a version above the one written in the header (layers,
        /// JPEG 2000, crypt filters of their own from 1.5, OpenType fonts from 1.6), it names that version in /Version, which counts where it
        /// is higher than the header (ISO 32000-2, 7.5.2 and 7.7.2, Table 29; decision F of 3.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 553).
        /// Never to 2.0: a 2.0 file saved as 1.7 on purpose keeps its values (8r.7).
        /// </summary>
        private void WriteCatalogObject()
        {
            if (_catalog is not { } catalog) return;
            _catalog = null;
            if (_contentVersion is { } needed && CompareVersions(needed, _version) > 0)
            {
                catalog.Set(PdfName.Version, PdfName.Get(needed));
                _version = needed;
            }
            if (_pdfA is not null) AssociatedCatalog(catalog);
            DeclareOwnExtension(catalog);   // PDF-Knead's own keys declared (Annex E.2, sentence 17; No. 793)
            // entries the standard wants as indirect references (ISO 32000-2, 7.7.2, Table 29): a source that had them direct keeps their
            // content, written as an object of its own (docs/FEHLERGEDAECHTNIS.md, No. 754)
            foreach (var key in IndirectCatalogEntries)
                if (catalog.GetRaw(key) is (PdfDictionary or PdfArray) and var value)
                {
                    var number = NewNumber();
                    WriteObject(number, value, null);
                    catalog.Set(key, new OutputReference(number));
                }
            WriteObject(_catalogNumber, catalog, null);
            DrainAll();
        }

        private static readonly PdfName[] IndirectCatalogEntries = [PdfName.Get("Dests"), PdfName.Get("Outlines"), PdfName.Get("Threads")];

        /// <summary>The lowest version the content written needs, if above the header's, and what needs it; see <see cref="Needs"/>.</summary>
        private string? _contentVersion, _contentReason;

        /// <summary>Notes that the content written needs <paramref name="version"/> (only below 2.0 and not for PDF/A, whose version is fixed).</summary>
        private void Needs(string version, string reason)
        {
            if (Pdf20 || _pdfA is not null || CompareVersions(version, _contentVersion ?? _version) <= 0) return;
            (_contentVersion, _contentReason) = (version, reason);
        }

        /// <summary>
        /// The label of each written page, from the labels of its source page; a source without labels numbers its pages from 1 in a range of
        /// their own. Null if no source has page labels.
        /// </summary>
        private Document.PageLabel[]? LabelsOfPages()
        {
            var bySource = new Dictionary<PdfDocument, IReadOnlyList<Document.PageLabel>?>(ReferenceEqualityComparer.Instance);
            var labelled = false;
            var result = new Document.PageLabel[_pages.Count];
            for (var i = 0; i < _pages.Count; i++)
            {
                var item = _pages[i];
                if (!bySource.TryGetValue(item.Source, out var labels)) bySource[item.Source] = labels = Document.PageLabels.Of(item.Source);
                var index = item.Page.Index;
                result[i] = labels is not null && index >= 0 && index < labels.Count ? labels[index] : new Document.PageLabel("D", "", Math.Max(0, index) + 1);
                labelled |= labels is not null;
            }
            return labelled ? result : null;
        }

        private void WriteInfo()
        {
            var info = new PdfDictionary();
            var cleanMetadata = _options.Cleanup.HasFlag(CleanupItems.Metadata);
            // Sanitised: no properties of the source, not even application and date (the origin records stay; they belong to the signatures)
            if (!cleanMetadata && _main.Trailer.GetDictionary(PdfName.Info) is { } original)
                foreach (var (key, value) in original.Entries) info.Set(key, Rebase(value, _sources[_main]));
            if (_infoFromXmp is not null)
                foreach (var (key, value) in _infoFromXmp) info.Set(PdfName.Get(key), new PdfString(PdfTextEncoding.Encode(value)));
            if (SealRecords() is { } seals) info.Set(SignaturesKey, new PdfString(PdfTextEncoding.Encode(seals)));
            else info.Remove(SignaturesKey);
            // the title (No. 492): from PDF 2.0 only in the XMP — except PDF/A, whose packet is built from this dictionary
            if (WantedTitle() is { } title)
            {
                if (title.Length == 0 || Pdf20 && _pdfA is null) info.Remove(TitleKey);
                else info.Set(TitleKey, new PdfString(PdfTextEncoding.Encode(title)));
            }
            if (!cleanMetadata)
            {
                // /Producer is deprecated in PDF 2.0 (ISO 32000-2, 14.3.3; the program stands in the XMP as pdf:Producer, No. 613): a 2.0 copy
                // brings no such entry of its own and only keeps the one of the source current, so that both say the same; PDF/A builds its
                // packet from this dictionary (docs/FEHLERGEDAECHTNIS.md, No. 810)
                if (!Pdf20 || _pdfA is not null || info.ContainsKey(ProducerKey))
                    info.Set(ProducerKey, new PdfString(PdfTextEncoding.Encode(Producer)));
                info.Set(ModDateKey, new PdfString(System.Text.Encoding.ASCII.GetBytes(PdfDate(Modified))));
            }
            if (_pdfA is not null) info = PdfAInfo(info);
            // PDF/A-4: only the date of the last change (ISO 19005-4, 6.1.3; the rest stands in the XMP metadata), also for a PDF/A-4 file
            // saved the ordinary way that keeps its identification (No. 707)
            if (_pdfA?.Part.Part() == 4 || KeepsPdfA4Claim())
            {
                var date = new PdfDictionary();
                date.Set(ModDateKey, new PdfString(System.Text.Encoding.ASCII.GetBytes(PdfDate(Modified))));
                info = date;
            }
            if (_infoNumber > 0) WriteObject(_infoNumber, info, null);
        }

        /// <summary>
        /// Origin of sealed signatures as a JSON list (page, line, identifier, public key, content hash, signature); null if
        /// there are none. Records that the source files already contain are taken over with the new page number (deleted
        /// pages: dropped; inserted files: theirs are added); then those of the new signatures. Unreadable entries are
        /// dropped instead of spoiling the list.
        /// </summary>
        private string? SealRecords()
        {
            var bySource = new Dictionary<Document.PdfDocument, ILookup<int, SealRecord>>(ReferenceEqualityComparer.Instance);
            var records = new List<SealRecord>();
            for (var i = 0; i < _pages.Count; i++)
            {
                var item = _pages[i];
                if (!bySource.TryGetValue(item.Source, out var known))
                    bySource[item.Source] = known = SealRecord.Read(item.Source).ToLookup(r => r.Page);
                foreach (var old in known[item.Page.Index + 1])
                    // If what the origin line showed (name, identifier) was redacted, the record goes too, as it names both
                    if (!(_removedText.TryGetValue(item, out var removed) && RevealsRemoved(old.Seal, removed))) records.Add(old with { Page = i + 1 });
            }
            for (var i = 0; i < _pages.Count; i++)
                foreach (var element in _pages[i].Elements)
                    if (element.Seal is { } seal) records.Add(new SealRecord(i + 1, seal));
            if (records.Count == 0) return null;

            using var buffer = new MemoryStream();
            using (var json = new System.Text.Json.Utf8JsonWriter(buffer))
            {
                json.WriteStartArray();
                foreach (var (page, seal) in records)
                {
                    json.WriteStartObject();
                    json.WriteNumber("seite", page);
                    json.WriteString("herkunft", seal.Origin);
                    json.WriteString("kennung", seal.Code);
                    json.WriteBase64String("schluessel", seal.PublicKey);
                    json.WriteBase64String("inhalt", seal.ContentHash);
                    json.WriteBase64String("signatur", seal.Signature);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            }
            return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        /// <summary>
        /// Does the record name something that the redaction removed from the page? Compared without white space: the identifier,
        /// or a word of the origin line with at least 3 characters (name, date), in the removed text.
        /// </summary>
        private static bool RevealsRemoved(Editing.SignatureSeal seal, string removed)
        {
            if (removed.Length == 0) return false;
            static string Squeeze(string s) => new([.. s.Where(c => !char.IsWhiteSpace(c))]);
            var text = Squeeze(removed);
            if (seal.Code.Length > 0 && text.Contains(Squeeze(seal.Code), StringComparison.OrdinalIgnoreCase)) return true;
            return seal.Origin.Split([' ', '·'], StringSplitOptions.RemoveEmptyEntries)
                .Any(word => word.Length >= 3 && text.Contains(word, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Date according to 7.9.4, in the form of the version written (2.0: without the closing apostrophe).</summary>
        internal string PdfDate(DateTimeOffset time) => Document.PdfDate.Format(time, CompareVersions(_version, "2.0") >= 0);

        // ------------------------------------------------------------ Bookmarks

        private sealed class OutlineNode
        {
            public PdfObject? Title;
            public PdfObject? Dest;
            public PdfObject? Action;
            public PdfObject? Color;
            public PdfObject? Flags;
            /// <summary>The other entries of the source's bookmark (its structure element, keys of other programs), rebased.</summary>
            public PdfDictionary? Rest;
            public bool Open;
            public List<OutlineNode> Kids = [];
            public int Number;
        }

        /// <summary>Bookmarks of all sources, reduced to the pages taken over; inserted files as a branch of their own.</summary>
        private int? WriteOutlines()
        {
            var top = new List<OutlineNode>();
            var count = 0;
            foreach (var source in _sourceOrder)
            {
                if (source.Doc.Catalog?.GetDictionary(PdfName.Outlines) is not { } root || _options.OutlinesLeftOut?.Contains(source.Doc) == true) continue;
                var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                var items = CollectOutline(source, root, visited, 0, ref count);
                if (items.Count == 0) continue;
                if (source.IsMain)
                {
                    top.AddRange(items);
                    continue;
                }
                // Branch with the file name; its target is the first page taken over from the file
                var name = source.Doc.FilePath is { } path ? Path.GetFileNameWithoutExtension(path) : Tr.T("Eingefügte Seiten");
                var first = _pages.Select((p, i) => (p, i)).First(x => ReferenceEquals(x.p.Source, source.Doc)).i;
                var page = new PdfArray([new OutputReference(PageNumberOf(first)), PdfName.Get("Fit")]);
                // in PDF 2.0 with its tree in the copy: also the part it hangs in as the structure destination (ISO 14289-2, 8.8; 6.5 e)
                var part = Pdf20 && source.FirstPart is { } firstPart ? new PdfArray([firstPart, PdfName.Get("Fit")]) : null;
                top.Add(new OutlineNode
                {
                    Title = new PdfString(PdfTextEncoding.Encode(name)),
                    Dest = part is null ? page : null,
                    Action = part is null ? null : GoTo(page, part),
                    Kids = items,
                });
            }
            if (top.Count == 0) return null;

            var rootNumber = NewNumber();
            Number(top);
            WriteOutlineLevel(top, rootNumber);
            var dict = new PdfDictionary();
            dict.Set(PdfName.Type, PdfName.Outlines);
            dict.Set(PdfName.First, new OutputReference(top[0].Number));
            dict.Set(Last, new OutputReference(top[^1].Number));
            dict.Set(PdfName.Count, PdfInteger.Of(Visible(top)));
            WriteObject(rootNumber, dict, null);
            return rootNumber;
        }

        private int PageNumberOf(int index)
        {
            // New number of the page object, from the source's page mapping (by object number, otherwise by page index)
            var item = _pages[index];
            var source = _sources[item.Source];
            return item.Page.Id is { } id && source.PageOut.TryGetValue(id.Number, out var n) ? n : source.PageOutByIndex[item.Page.Index];
        }

        private List<OutlineNode> CollectOutline(Source source, PdfDictionary parent, HashSet<PdfDictionary> visited, int depth, ref int count)
        {
            var result = new List<OutlineNode>();
            if (depth > 32) return result;
            for (var item = parent.GetDictionary(PdfName.First); item is not null && count < 100_000; item = item.GetDictionary(Next))
            {
                if (!visited.Add(item)) break;
                count++;
                var kids = CollectOutline(source, item, visited, depth + 1, ref count);
                var action = item.GetDictionary(A);
                var isGoTo = action?.GetName(S)?.Value == "GoTo";
                var target = item.Get(Dest) is not PdfNull ? item.Get(Dest) : isGoTo ? action!.Get(D) : PdfNull.Instance;
                PdfObject? dest = null;
                if (target is not PdfNull && PdfOutline.ExplicitDestination(source.Doc, target) is { Count: > 0 } explicitDest &&
                    TargetPage(source, explicitDest[0]) is { } page)
                {
                    var array = new PdfArray([new OutputReference(page)]);
                    for (var i = 1; i < explicitDest.Count; i++) array.Add(Rebase(explicitDest[i], source));
                    dest = array;
                }
                var otherAction = action is not null && !isGoTo;
                // an entry whose page is not taken over goes (with no entries below it); one that never had a target stays — a heading of the
                // outline or a note (ISO 32000-2, 12.3.3 allows it); before, every save that builds the bookmarks anew dropped those (No. 679)
                var hadTarget = target is not PdfNull || action is not null;
                if (hadTarget && dest is null && !otherAction && kids.Count == 0) continue;
                // a GoTo with a structure destination keeps it where its element is written (PDF 2.0; ISO 32000-2, 12.3.2.3; 6.5 e, No. 643) —
                // before, every GoTo became a plain /Dest
                var structureDest = isGoTo && dest is not null ? StructureDestination(source, action!) : null;
                // what else the bookmark has stays: its structure element where the copy writes it (/SE, Table 151), keys of other programs —
                // they went with every save that builds the bookmarks anew (docs/FEHLERGEDAECHTNIS.md, No. 880)
                PdfDictionary? rest = null;
                foreach (var (key, value) in item.Entries)
                    if (!OutlineBuilt.Contains(key.Value) && !(key == StructureElementKey && !WritesElement(source, value)))
                        (rest ??= new PdfDictionary()).Set(key, Rebase(value, source));
                result.Add(new OutlineNode
                {
                    Title = item.Get(Title) as PdfString ?? new PdfString([]),
                    Dest = structureDest is null ? dest : null,
                    Action = otherAction ? Rebase(action!, source) : structureDest is not null ? GoTo(dest!, structureDest) : null,
                    Color = item.Get(C) as PdfArray,
                    Flags = item.Get(PdfName.F) as PdfInteger,
                    Rest = rest,
                    Open = (item.GetInteger(PdfName.Count) ?? 0) > 0,
                    Kids = kids,
                });
            }
            return result;
        }

        /// <summary>The entries of a bookmark the writer builds itself (<see cref="WriteOutlineLevel"/>); the others are taken over.</summary>
        private static readonly HashSet<string> OutlineBuilt = new(StringComparer.Ordinal)
            { "Title", "Parent", "Prev", "Next", "First", "Last", "Count", "Dest", "A", "C", "F" };

        private static readonly PdfName DestsKey = PdfName.Get("Dests");

        /// <summary>
        /// Whether a destination as stored (an array, a dictionary with /D, or a reference to one) names a page of the source that is not
        /// written (No. 692). Names, remote and structure destinations: no.
        /// </summary>
        private static bool DestinationGone(Source source, PdfObject? value)
        {
            for (var depth = 0; depth < 4 && value is not null; depth++)
                switch (value)
                {
                    case PdfReference r:
                        value = source.Doc.Resolve(r.Id);
                        break;
                    case PdfDictionary d:
                        value = d.GetRaw(D);
                        break;
                    case PdfArray { Count: > 0 } array:
                        return array[0] is PdfReference page && source.TreeNodes.Contains(page.Id.Number) && !source.PageOut.ContainsKey(page.Id.Number);
                    default:
                        return false;
                }
            return false;
        }

        /// <summary>
        /// Links on the pages written whose destination — explicit, or a name the file resolves — is a page not written: they lose /Dest (and a
        /// GoTo action, /A), as overrides of their source; the link stays, leading nowhere, rather than to null (ISO 32000-2, 12.3.2.2 and
        /// 12.5.6.5: /Dest and /A are optional; No. 692). Before they named null as their page.
        /// </summary>
        private void LinksWithoutGoneDestinations()
        {
            foreach (var item in _pages)
            {
                if (item.Page.Dictionary.Get(Annots) is not PdfArray annots) continue;
                var source = _sources[item.Source];
                if (source.TreeNodes.Count == 0) continue;
                for (var i = 0; i < annots.Count && i < 10_000; i++)
                {
                    if (annots[i] is not PdfReference reference) continue;
                    var current = source.Overrides.TryGetValue(reference.Id.Number, out var o) ? o : source.Doc.Resolve(reference.Id);
                    if (current is not PdfDictionary link || link.GetName(PdfName.Subtype)?.Value != "Link") continue;
                    var action = link.GetDictionary(A);
                    var goTo = action?.GetName(S)?.Value == "GoTo";
                    var target = link.Get(Dest) is { } own and not PdfNull ? own : goTo ? action!.Get(D) : null;
                    if (target is null) continue;
                    var explicitDest = PdfOutline.ExplicitDestination(source.Doc, target);
                    if (explicitDest is null || !DestinationGone(source, explicitDest)) continue;
                    var copy = Clone(link);
                    copy.Remove(Dest);
                    if (goTo) copy.Remove(A);
                    source.Overrides[reference.Id.Number] = copy;
                }
            }
        }

        /// <summary>New number of the target page, if it is taken over.</summary>
        private static int? TargetPage(Source source, PdfObject target) => target switch
        {
            PdfReference r when source.PageOut.TryGetValue(r.Id.Number, out var n) => n,
            PdfInteger i when i.Value is >= 0 and <= int.MaxValue && source.PageOutByIndex.TryGetValue((int)i.Value, out var n) => n,
            _ => null,
        };

        private void Number(List<OutlineNode> nodes)
        {
            foreach (var node in nodes)
            {
                node.Number = NewNumber();
                Number(node.Kids);
            }
        }

        /// <summary>Visible descendants when all open branches are expanded (for /Count).</summary>
        private static int Visible(List<OutlineNode> nodes) => nodes.Sum(n => 1 + (n.Open ? Visible(n.Kids) : 0));

        private void WriteOutlineLevel(List<OutlineNode> nodes, int parent)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                var dict = new PdfDictionary();
                dict.Set(Title, node.Title!);
                dict.Set(PdfName.Parent, new OutputReference(parent));
                if (i > 0) dict.Set(PrevKey, new OutputReference(nodes[i - 1].Number));
                if (i + 1 < nodes.Count) dict.Set(Next, new OutputReference(nodes[i + 1].Number));
                if (node.Kids.Count > 0)
                {
                    dict.Set(PdfName.First, new OutputReference(node.Kids[0].Number));
                    dict.Set(Last, new OutputReference(node.Kids[^1].Number));
                    var visible = Visible(node.Kids);
                    dict.Set(PdfName.Count, PdfInteger.Of(node.Open ? visible : -visible));
                }
                if (node.Dest is not null) dict.Set(Dest, node.Dest);
                if (node.Action is not null) dict.Set(A, node.Action);
                if (node.Color is not null) dict.Set(C, node.Color);
                if (node.Flags is not null) dict.Set(PdfName.F, node.Flags);
                if (node.Rest is { } rest)
                    foreach (var (key, value) in rest.Entries) dict.Set(key, value);
                WriteObject(node.Number, dict, null);
                WriteOutlineLevel(node.Kids, node.Number);
            }
        }

        // ------------------------------------------------------------ Form

        /// <summary>
        /// Fields whose widgets lie on pages taken over; fields of inserted files with a name that is already taken are
        /// renamed ("Name_2"), otherwise they would share their value.
        /// </summary>
        private PdfDictionary? BuildAcroForm()
        {
            var fields = new PdfArray();
            var names = new HashSet<string>(StringComparer.Ordinal);
            PdfDictionary? result = null;
            foreach (var source in _sourceOrder)
            {
                if (source.Doc.Catalog?.GetDictionary(AcroForm) is not { } form) continue;
                if (source.IsMain)
                {
                    result = new PdfDictionary();
                    foreach (var (key, value) in form.Entries)
                    {
                        if (key == Fields || key == Xfa || key == CalculationOrder) continue;
                        // Default resources as a direct object, so that fonts of inserted files and new fields can be added
                        result.Set(key, key == Dr && form.Get(Dr) is PdfDictionary dr ? DirectResources(dr, source) : Rebase(value, source));
                    }
                }
                var widgets = new HashSet<int>();
                foreach (var item in source.Items)
                    if (item.Page.Dictionary.Get(Annots) is PdfArray annots)
                        for (var i = 0; i < annots.Count; i++)
                            if (annots[i] is PdfReference r && !(annots.Get(i) is PdfDictionary own && OwnedAnnotations().Contains(own))) widgets.Add(r.Id.Number);
                // Calculation order only with fields that are included (otherwise fields of deleted pages, with their values, would come along via /CO)
                if (source.IsMain && result is not null && form.Get(CalculationOrder) is PdfArray order)
                {
                    var co = new PdfArray();
                    for (var i = 0; i < order.Count && i < 10_000; i++)
                        if (order[i] is PdfReference calculated && HasWidget(source.Doc, calculated.Id.Number, widgets, 0, [])) co.Add(Rebase(order[i], source));
                    result.Set(CalculationOrder, co);
                }
                if (form.GetArray(Fields) is not { } list) continue;
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] is not PdfReference field || !HasWidget(source.Doc, field.Id.Number, widgets, 0, [])) continue;
                    if (source.IsMain && _noLongerFields.Contains(field.Id.Number)) continue;   // a widget that is now a kid of a linked field
                    var dict = (source.Overrides.GetValueOrDefault(field.Id.Number) ?? source.Doc.GetObject(field.Id.Number)) as PdfDictionary;
                    var name = dict?.GetString(T)?.ToText();
                    if (!source.IsMain && dict is not null && name is not null && names.Contains(name))
                    {
                        var k = 2;
                        while (names.Contains($"{name}_{k}")) k++;
                        name = $"{name}_{k}";
                        var renamed = Clone(dict);
                        renamed.Set(T, new PdfString(PdfTextEncoding.Encode(name)));
                        source.Overrides[field.Id.Number] = renamed;
                    }
                    if (name is not null) names.Add(name);
                    var number = Map(source, field.Id.Number);
                    if (number > 0) fields.Add(new OutputReference(number));
                }
                // the defaults its fields took from its own form (/DA, /Q), where the copy's form has other ones or none (No. 690)
                if (!source.IsMain) PushFormDefaults(source, form, result);
                // Take the form fonts of inserted files over into the default resources
                if (!source.IsMain && form.GetDictionary(Dr)?.GetDictionary(Font) is { } fonts)
                {
                    result ??= new PdfDictionary();
                    if (result.Get(Dr) is not PdfDictionary dr) result.Set(Dr, dr = new PdfDictionary());
                    if (dr.Get(Font) is not PdfDictionary target) dr.Set(Font, target = new PdfDictionary());
                    foreach (var (key, value) in fonts.Entries)
                        if (!target.ContainsKey(key) && value is PdfReference) target.Set(key, Rebase(value, source));
                }
            }
            if (result is null && fields.Count == 0) return null;
            result ??= new PdfDictionary();
            result.Set(Fields, fields);
            return result;
        }

        private static readonly PdfName QKey = PdfName.Get("Q"), FtKey = PdfName.Get("FT");

        /// <summary>
        /// The form defaults of an inserted file (/DA, /Q of its /AcroForm: inherited by fields of variable text, ISO 32000-2, 12.7.4.3) that the
        /// copy's form does not give alike, set on its terminal fields of variable text (text fields, choice fields) that name none themselves
        /// or through a parent — before, they went with the inserted file's form: its text fields had no default appearance left (or the
        /// copy's, in another size or colour), which the norm requires for them (docs/FEHLERGEDAECHTNIS.md, No. 690; found by the run of all
        /// ways to write). Set as overrides of the inserted file; at most 32 levels, each field once.
        /// </summary>
        private static void PushFormDefaults(Source source, PdfDictionary form, PdfDictionary? copyForm)
        {
            var pushed = new List<(PdfName Key, PdfObject Value)>();
            foreach (var key in (ReadOnlySpan<PdfName>)[Da, QKey])
                if (form.Get(key) is { } value and not PdfNull &&
                    !(copyForm?.Get(key) is { } given && PdfSerializer.ToBytes(given).AsSpan().SequenceEqual(PdfSerializer.ToBytes(value))))
                    pushed.Add((key, value));
            if (pushed.Count == 0 || form.GetArray(Fields) is not { } list) return;
            var seen = new HashSet<int>();
            void Push(PdfReference reference, HashSet<PdfName> named, string? type, int depth)
            {
                if (depth > 32 || !seen.Add(reference.Id.Number)) return;
                if ((source.Overrides.GetValueOrDefault(reference.Id.Number) ?? source.Doc.GetObject(reference.Id.Number)) is not PdfDictionary field) return;
                type = field.GetName(FtKey)?.Value ?? type;
                var own = new HashSet<PdfName>(named);
                foreach (var (key, _) in pushed)
                    if (field.ContainsKey(key)) own.Add(key);
                // kids that are fields (with a name) go on; kids that are widgets only (no /T) make this field terminal
                var fieldKids = new List<PdfReference>();
                if (field.Get(KidsKey) is PdfArray kids)
                    for (var i = 0; i < kids.Count && i < 10_000; i++)
                        if (kids[i] is PdfReference kid && kids.Get(i) is PdfDictionary kidDict && kidDict.ContainsKey(T)) fieldKids.Add(kid);
                if (fieldKids.Count > 0)
                {
                    foreach (var kid in fieldKids) Push(kid, own, type, depth + 1);
                    return;
                }
                if (type is not ("Tx" or "Ch")) return;
                var missing = pushed.Where(p => !own.Contains(p.Key)).ToList();
                if (missing.Count == 0) return;
                var copy = Clone(field);
                foreach (var (key, value) in missing) copy.Set(key, value);
                source.Overrides[reference.Id.Number] = copy;
            }
            for (var i = 0; i < list.Count && i < 100_000; i++)
                if (list[i] is PdfReference top) Push(top, [], null, 0);
        }

        /// <summary>Resource dictionary as a direct copy, /Font direct as well (so that it can be extended).</summary>
        private PdfDictionary DirectResources(PdfDictionary resources, Source source)
        {
            var copy = new PdfDictionary(resources.Count + 1);
            foreach (var (key, value) in resources.Entries)
                copy.Set(key, key == Font && resources.Get(Font) is PdfDictionary fonts ? Rebase(fonts, source) : Rebase(value, source));
            return copy;
        }

        /// <summary>Form of the main document unchanged (same page sequence), with /Fields and /DR as direct objects.</summary>
        private PdfDictionary MainAcroForm()
        {
            var main = _sources[_main];
            var result = new PdfDictionary();
            if (_main.Catalog?.GetDictionary(AcroForm) is not { } form) return result;
            foreach (var (key, value) in form.Entries)
            {
                if (key == Xfa) continue;   // XFA would not know the new fields
                if (key == Fields)
                {
                    // Without own fields: WithNewFields writes them anew with their current state
                    var kept = new PdfArray();
                    if (form.Get(Fields) is PdfArray list)
                        for (var i = 0; i < list.Count; i++)
                            // without own and deleted fields (including parent fields that have become empty: in Removed)
                            if (!(list.Get(i) is PdfDictionary field && OwnedAnnotations().Contains(field)) &&
                                !(list[i] is PdfReference r && (main.Removed.Contains(r.Id.Number) || _noLongerFields.Contains(r.Id.Number))))
                                kept.Add(Rebase(list[i], main));
                    result.Set(key, kept);
                }
                else if (key == Dr && form.Get(Dr) is PdfDictionary dr) result.Set(key, DirectResources(dr, main));
                // Calculation order as a direct object (as a reference, WithNewFields would not find it and would replace it)
                else if (key == CalculationOrder && form.Get(CalculationOrder) is PdfArray order)
                {
                    // without fields that were dropped (under a redaction, deleted); they would otherwise appear as null in it
                    var co = new PdfArray();
                    for (var i = 0; i < order.Count && i < 10_000; i++)
                        if (!(order[i] is PdfReference r && main.Removed.Contains(r.Id.Number))) co.Add(Rebase(order[i], main));
                    result.Set(key, co);
                }
                else result.Set(key, Rebase(value, main));
            }
            return result;
        }

        /// <summary>Adds new fields (from elements) to the form, together with their standard fonts in /DR.</summary>
        private PdfDictionary WithNewFields(PdfDictionary form)
        {
            if (form.Get(Fields) is not PdfArray fields) form.Set(Fields, fields = new PdfArray());
            foreach (var number in _newFields) fields.Add(new OutputReference(number));
            if (form.Get(Dr) is not PdfDictionary dr) form.Set(Dr, dr = new PdfDictionary());
            if (dr.Get(Font) is not PdfDictionary fonts) dr.Set(Font, fonts = new PdfDictionary());
            foreach (var (key, baseFont) in (ReadOnlySpan<(string, string)>)[("Helv", "Helvetica"), ("TiRo", "Times-Roman"), ("Cour", "Courier"), ("ZaDb", "ZapfDingbats")])
            {
                if (fonts.ContainsKey(PdfName.Get(key))) continue;
                var font = new PdfDictionary();
                font.Set(PdfName.Type, Font);
                font.Set(PdfName.Subtype, PdfName.Get("Type1"));
                font.Set(PdfName.Get("BaseFont"), PdfName.Get(baseFont));
                if (key != "ZaDb") font.Set(PdfName.Get("Encoding"), PdfName.Get("WinAnsiEncoding"));   // symbol font: built-in encoding
                fonts.Set(PdfName.Get(key), font);
            }
            // Form fonts as separate objects, as common PDF editors write them: a widely used reader does not find fonts entered directly when it
            // redraws a changed field, and the number stays invisible. See docs/FEHLERGEDAECHTNIS.md, No. 50.
            foreach (var (key, value) in fonts.Entries.ToList())
                if (value is PdfDictionary direct)
                {
                    var number = NewNumber();
                    _memoryQueue.Enqueue((number, direct));
                    fonts.Set(key, new OutputReference(number));
                }
            if (!form.ContainsKey(Da)) form.Set(Da, new PdfString("/Helv 0 Tf 0 g"u8.ToArray()));
            // Calculation order (/CO, ISO 32000-2, 12.7.3, Table 224): without an entry, widely used readers do not calculate a field. The
            // existing order first, then the calculations set or changed here — fields of the file and new ones together, each after the
            // fields it reads (docs/FEHLERGEDAECHTNIS.md, No. 578); a calculation of the file that was not in /CO stays out, as in the file.
            // A field of the file whose calculation changed leaves its old place and comes with the others, after what it reads — as the
            // program has calculated it (No. 588)
            if (_newCalculated.Count > 0 || _calculatedForeign.Count > 0)
            {
                var co = new PdfArray();
                var added = new List<(int Number, string Name, FieldCalculation Calc)>();
                foreach (var (source, old, name, calc) in _calculatedForeign)
                    if (Map(source, old) is var n && n > 0) added.Add((n, name, calc));
                added.AddRange(_newCalculated);
                var seen = added.Select(a => a.Number).ToHashSet();
                if (form.GetRaw(CalculationOrder) is PdfArray existing)
                    for (var i = 0; i < existing.Count; i++)
                        if (existing[i] is OutputReference r && seen.Add(r.Number)) co.Add(r);
                seen.Clear();
                foreach (var r in co) if (r is OutputReference kept) seen.Add(kept.Number);
                foreach (var (n, _, _) in FieldCalculation.DependencyOrder(added, a => a.Name, a => a.Calc))
                    if (seen.Add(n)) co.Add(new OutputReference(n));
                form.Set(CalculationOrder, co);
            }
            return form;
        }

        private static readonly PdfName CalculationOrder = PdfName.Get("CO");

        private static bool HasWidget(PdfDocument doc, int number, HashSet<int> widgets, int depth, HashSet<int> visited)
        {
            if (widgets.Contains(number)) return true;
            if (depth > 32 || !visited.Add(number)) return false;
            if (doc.GetObject(number) is not PdfDictionary dict || dict.GetArray(PdfName.Kids) is not { } kids) return false;
            for (var i = 0; i < kids.Count; i++)
                if (kids[i] is PdfReference kid && HasWidget(doc, kid.Id.Number, widgets, depth + 1, visited)) return true;
            return false;
        }

        // ------------------------------------------------------------ Layers

        /// <summary>
        /// The copy has no layers (PDF/A-1, ISO 19005-1, 6.1.13): what the display hides of them is dropped — content and annotations —, the
        /// rest drawn as it is shown, and the file names no layers (/OCProperties).
        /// </summary>
        private bool FlattensLayers => _pdfA?.Part.Part() == 1;

        /// <summary>
        /// Sanitising with hidden layers: their content is dropped, and so are the layers themselves; otherwise their name
        /// ("Layer salary list …") would remain in /OCProperties. References to them become null and are removed from the lists. A layer named
        /// in a visibility expression stays: a null in /VE would make the expression invalid, and content it hides on screen would show
        /// (docs/FEHLERGEDAECHTNIS.md, No. 590).
        /// </summary>
        private void DropHiddenLayers()
        {
            foreach (var source in _sourceOrder)
            {
                if (source.Doc.Catalog?.GetDictionary(OCProperties)?.GetArray(Ocgs) is not { } groups) continue;
                HashSet<PdfDictionary>? inExpressions = null;
                for (var i = 0; i < groups.Count && i < 10_000; i++)
                    if (groups[i] is PdfReference reference && groups.Get(i) is PdfDictionary group && source.NeverShownContent.IsHidden(group, null)
                        && !(inExpressions ??= DocumentCleanup.LayersInExpressions(source.Doc)).Contains(group))
                        source.Removed.Add(reference.Id.Number);
            }
        }

        /// <summary>Removes dropped layers (null) from the lists of /OCProperties, including nested ones (/Order).</summary>
        private static PdfObject WithoutNulls(PdfObject obj, int depth = 0)
        {
            switch (obj)
            {
                case PdfArray array when depth < 16:
                    var kept = new PdfArray();
                    for (var i = 0; i < array.Count; i++)
                        if (array[i] is not PdfNull) kept.Add(WithoutNulls(array[i], depth + 1));
                    return kept;
                case PdfDictionary dict when depth < 16:
                    var copy = new PdfDictionary(dict.Count);
                    foreach (var (key, value) in dict.Entries) copy.Set(key, WithoutNulls(value, depth + 1));
                    return copy;
                default:
                    return obj;
            }
        }

        /// <summary>Layers (optional content): those of the main document, supplemented by those of inserted files.</summary>
        private PdfDictionary? OptionalContent()
        {
            var merged = OptionalContentMerged();
            return merged is not null && _options.Cleanup.HasFlag(CleanupItems.HiddenLayers) ? (PdfDictionary)WithoutNulls(merged) : merged;
        }

        private PdfDictionary? OptionalContentMerged()
        {
            PdfDictionary? result = null;
            PdfArray? ocgs = null;
            PdfDictionary? config = null;
            foreach (var source in _sourceOrder)
            {
                if (source.Doc.Catalog?.GetDictionary(OCProperties) is not { } oc) continue;
                if (source.IsMain)
                {
                    // Take /OCGs and /D over resolved; inserted layers are appended below
                    result = new PdfDictionary();
                    foreach (var (key, value) in oc.Entries)
                    {
                        // further configurations go with „Weitere Dokumentdaten“ (built here, not through the sanitising filter)
                        if (DocumentCleanup.DropsKey(key, value, _options.Cleanup, source.Doc)) continue;
                        // naming the configurations (repair of the accessibility check) needs them as copies of their own, not references
                        if (_options.NameLayerConfigurations && key.Value == "Configs" && oc.Get(key) is PdfArray configs)
                            result.Set(key, new PdfArray([.. Enumerable.Range(0, Math.Min(configs.Count, 1000))
                                .Select(i => configs.Get(i) is PdfDictionary c ? Rebase(c, source) : Rebase(configs[i], source))]));
                        else result.Set(key, key == Ocgs || key == D ? Rebase(oc.Get(key), source) : Rebase(value, source));
                    }
                    continue;
                }
                if (source.Items.Count == 0) continue;
                result ??= new PdfDictionary();
                ocgs ??= result.Get(Ocgs) as PdfArray ?? new PdfArray();
                result.Set(Ocgs, ocgs);
                config ??= result.Get(D) as PdfDictionary ?? new PdfDictionary();
                result.Set(D, config);
                if (oc.GetArray(Ocgs) is { } list)
                    for (var i = 0; i < list.Count; i++) ocgs.Add(Rebase(list[i], source));
                if (oc.GetDictionary(D) is { } other)
                {
                    foreach (var key in new[] { Off, Order })
                    {
                        if (other.GetArray(key) is not { } values) continue;
                        if (config.Get(key) is not PdfArray target) config.Set(key, target = new PdfArray());
                        for (var i = 0; i < values.Count; i++) target.Add(Rebase(values[i], source));
                    }
                    // the first inserted file that names its default configuration names the merged one
                    if (!Named(config) && Named(other)) config.Set(PdfName.Name, new PdfString(other.GetString(PdfName.Name)!.Bytes));
                }
            }
            // a default configuration without a name or with an empty one (only inserted files had layers, none named it): „Standard“ — readers show the name, and
            // the checks of PDF/UA ask for it (ISO 14289-1, 7.10; ISO 14289-2, 8.7; docs/FEHLERGEDAECHTNIS.md, No. 653); before a new /D had
            // /OFF and /Order only
            if (config is not null && !Named(config)) config.Set(PdfName.Name, new PdfString(PdfTextEncoding.Encode(Tr.T("Standard"))));
            if (_options.NameLayerConfigurations && result is not null) NameConfigurations(result);
            return result;
        }

        /// <summary>
        /// The repair „Ebenen-Ansichten benennen“ (<see cref="PdfSaveOptions.NameLayerConfigurations"/>): the default configuration and those of
        /// /Configs without a name (or with an empty one) get one, distinct from the names already there.
        /// </summary>
        private void NameConfigurations(PdfDictionary layers)
        {
            var configsKey = PdfName.Get("Configs");
            var taken = new HashSet<string>(StringComparer.Ordinal);
            var all = new List<PdfDictionary>();
            if (layers.Get(D) is PdfDictionary standard) all.Add(standard);
            // the configurations of /Configs are copies of their own here (taken over resolved, OptionalContentMerged)
            if (layers.Get(configsKey) is PdfArray configs)
                for (var i = 0; i < configs.Count && i < 1000; i++)
                    if (configs.Get(i) is PdfDictionary config) all.Add(config);
            foreach (var config in all)
                if (Named(config)) taken.Add(config.GetString(PdfName.Name)!.ToText().Trim());
            foreach (var config in all)
            {
                if (Named(config)) continue;
                var fallback = ReferenceEquals(config, layers.Get(D)) ? Tr.T("Standard") : Tr.T("Ansicht");
                var name = fallback;
                for (var n = 2; !taken.Add(name); n++) name = $"{fallback} {n}";
                config.Set(PdfName.Name, new PdfString(PdfTextEncoding.Encode(name)));
            }
        }


        /// <summary>Whether a configuration of the layers has a name that says something (an empty one counts as none, ISO 14289-1, 7.10).</summary>
        private static bool Named(PdfDictionary config) => config.GetString(PdfName.Name)?.ToText().Trim().Length > 0;

        // ------------------------------------------------------------ Redaction

        /// <summary>Redacted pages with the annotations that are dropped in the process.</summary>
        private readonly List<(PageItem Item, PdfDictionary Original, HashSet<PdfDictionary> Dropped)> _redacted = [];

        /// <summary>The removed text per redacted page (for the origin records, <see cref="SealRecords"/>).</summary>
        private readonly Dictionary<PageItem, string> _removedText = new(ReferenceEqualityComparer.Instance);

        /// <summary>Originals replaced by redaction or sanitising (images, forms, patterns) per source.</summary>
        private readonly Dictionary<PdfDocument, HashSet<int>> _replaced = new(ReferenceEqualityComparer.Instance);

        /// <summary>Fonts per source that lost characters through redaction or sanitising.</summary>
        private readonly Dictionary<PdfDocument, HashSet<int>> _touchedFonts = new(ReferenceEqualityComparer.Instance);

        private void AddTouched(PdfDocument doc, IReadOnlySet<int> fonts)
        {
            if (fonts.Count == 0) return;
            if (!_touchedFonts.TryGetValue(doc, out var set)) _touchedFonts[doc] = set = [];
            set.UnionWith(fonts);
        }

        /// <summary>
        /// The fonts that lost characters keep only what the pages of the new file still show (<see cref="FontScrub"/>; docs/FEHLERGEDAECHTNIS.md,
        /// No. 169): a character that was only in the redacted text could otherwise be read from the font.
        /// </summary>
        private void ScrubFonts()
        {
            foreach (var (doc, fonts) in _touchedFonts)
            {
                var source = AddSource(doc);
                var pages = _pages.Where(p => ReferenceEquals(p.Source, doc)).Select(p => p.Page).ToList();
                foreach (var (number, value) in FontScrub.Overrides(doc, pages, fonts, _cancel, source.Overrides))
                    source.Overrides.TryAdd(number, value);
            }
        }

        private void AddReplaced(PdfDocument doc, IReadOnlySet<int> objects)
        {
            if (objects.Count == 0) return;
            if (!_replaced.TryGetValue(doc, out var set)) _replaced[doc] = set = [];
            set.UnionWith(objects);
        }

        /// <summary>
        /// Leaves out replaced originals if no page of the new file draws them any more. Otherwise they would remain in the
        /// file via other paths: resources shared by pages (also inherited from the /Pages node), structure references (/Stm);
        /// a redacted image would be readable in its original form with any image extraction tool. References to them become null.
        /// </summary>
        private void DropReplacedOriginals()
        {
            var used = new Dictionary<PdfDocument, HashSet<int>>(ReferenceEqualityComparer.Instance);
            foreach (var item in _pages)
            {
                if (!_replaced.TryGetValue(item.Source, out var replaced) || !ResourceUsage.MayUse(item.Source, item.Page, replaced)) continue;
                _cancel.ThrowIfCancellationRequested();
                if (!used.TryGetValue(item.Source, out var set)) used[item.Source] = set = [];
                ResourceUsage.PageObjects(item.Source, item.Page, set, AddSource(item.Source).Overrides);
            }
            // Edited text drawn with a font of the file uses its glyphs as well: removing the old text made a Type 3 font with only the glyphs
            // still shown on the page, but the new text refers to the original font — a glyph only in the old word and in the new text went
            // and was drawn empty (phase 8.1, docs/FEHLERGEDAECHTNIS.md, No. 606)
            foreach (var item in _pages)
                foreach (var element in item.Elements)
                {
                    if (element is not RichTextElement || ElementAppearance.For(element) is not { OriginalFonts.Count: > 0 } appearance ||
                        appearance.Form.RawData is not { } body) continue;
                    foreach (var doc in appearance.OriginalFonts.Values.Select(f => f.Document).Distinct())
                    {
                        if (!_replaced.ContainsKey(doc)) continue;
                        _cancel.ThrowIfCancellationRequested();
                        if (!used.TryGetValue(doc, out var set)) used[doc] = set = [];
                        ResourceUsage.Objects(doc, body, appearance.Form.Dictionary.GetDictionary(PdfName.Resources), set);
                    }
                }
            foreach (var (doc, replaced) in _replaced)
            {
                var source = AddSource(doc);
                foreach (var number in replaced)
                {
                    if (!used.TryGetValue(doc, out var set) || !set.Contains(number)) source.Removed.Add(number);
                    // a redacted JBIG2 image's shared symbol dictionary holds the shapes of its characters: it goes, and the images still
                    // using it are written without it (docs/FEHLERGEDAECHTNIS.md, No. 353)
                    if (doc.HasObject(number) && doc.GetObject(number) is PdfStream image && Jbig2GlobalsOf(image.Dictionary) is { } globals)
                    {
                        if (!_sharedGlobals.TryGetValue(doc, out var dictionaries)) _sharedGlobals[doc] = dictionaries = [];
                        dictionaries.Add(globals);
                    }
                }
            }
        }

        /// <summary>Symbol dictionaries (/JBIG2Globals, by object number) of redacted images, per document: left out of the file.</summary>
        private readonly Dictionary<PdfDocument, HashSet<int>> _sharedGlobals = new(ReferenceEqualityComparer.Instance);

        /// <summary>The object number of a JBIG2 image's /JBIG2Globals; null without one.</summary>
        private static int? Jbig2GlobalsOf(PdfDictionary dict)
        {
            var parms = dict.Get(PdfName.DecodeParms) switch
            {
                PdfDictionary d => d,
                PdfArray { Count: > 0 } a => a.Get(a.Count - 1) as PdfDictionary,
                _ => null,
            };
            return parms?.GetRaw(PdfName.Get("JBIG2Globals")) is PdfReference r ? r.Id.Number : null;
        }

        /// <summary>
        /// A JBIG2 image whose symbol dictionary goes with a redaction (<see cref="_sharedGlobals"/>), as 1-bit samples packed with Flate —
        /// the same pixels, without the dictionary; null if it uses none of them or cannot be read.
        /// </summary>
        private (byte[] Data, PdfDictionary Dict)? WithoutSharedGlobals(PdfStream stream, PdfDocument doc, PdfDictionary original)
        {
            if (!_sharedGlobals.TryGetValue(doc, out var dictionaries) || Jbig2GlobalsOf(original) is not { } globals || !dictionaries.Contains(globals))
                return null;
            var width = (int)Math.Clamp(original.GetInteger(PdfName.Get("Width")) ?? 0, 0, int.MaxValue);
            var height = (int)Math.Clamp(original.GetInteger(PdfName.Get("Height")) ?? 0, 0, int.MaxValue);
            if (width <= 0 || height <= 0) return null;
            try
            {
                var samples = Graphics.PdfImageDecoder.Jbig2(doc.DecodeStream(stream), doc, width, height).Data;
                var dict = Clone(original);
                dict.Remove(PdfName.Length);
                dict.Remove(PdfName.DecodeParms);
                dict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
                return (Compress(samples).ToArray(), dict);
            }
            catch (Exception e) when (e is PdfException or InvalidDataException)
            {
                return null;
            }
        }
        private readonly Dictionary<PdfDictionary, HashSet<PdfDictionary>> _redactionDrops = new(ReferenceEqualityComparer.Instance);
        private readonly List<int> _idleHidden = [];

        /// <summary>The redacted pages (index in the written file) and what the check of the written file needs (No. 461).</summary>
        private readonly List<(int Page, Redactor.WrittenCheck Check)> _writtenChecks = [];
        private readonly List<int> _partlyShown = [];
        private readonly Dictionary<int, IReadOnlyList<PdfRectangle>> _barExtensions = [];
        private static readonly PdfName TabsKey = PdfName.Get("Tabs");
        private bool? _tagged;
        private bool _tabOrderByStructure;

        /// <summary>The file is tagged: /MarkInfo /Marked true and a structure tree (as ISO 14289-1, 7.1 reads it).</summary>
        private bool Tagged() => _tagged ??= Accessibility.StructureRoles.IsTagged(_main.Catalog);
        private IReadOnlyList<string> _droppedExtensions = [];

        // the document parts went: the page sequence changed and the hierarchy as read could not be followed (No. 790)
        private bool _documentPartsDropped;

        /// <summary>
        /// Applies redactions: pages with redaction elements get the redacted content (<see cref="Redactor"/>); the marks are
        /// used up by this, and other elements under the areas are dropped. Thumbnail and private data of the page (/Thumb,
        /// /PieceInfo) would show the old content, so they are dropped as well. Marks of hidden content (<see cref="RedactionElement.Hidden"/>)
        /// first remove only the characters nobody sees in their areas, without a bar (<see cref="Redactor.RemoveHiddenIn"/>); the bars
        /// then apply to the page that results.
        /// </summary>
        private IReadOnlyList<PageItem> ApplyRedactions(IReadOnlyList<PageItem> pages)
        {
            List<PageItem>? result = null;
            for (var i = 0; i < pages.Count; i++)
            {
                var item = pages[i];
                var marks = item.Elements.OfType<RedactionElement>().ToList();
                var areas = marks.Where(e => !e.Hidden).SelectMany(e => e.PageAreas).Where(a => !a.IsEmpty).ToList();
                var hiddenAreas = marks.Where(e => e.Hidden).SelectMany(e => e.PageAreas).Where(a => !a.IsEmpty).ToList();
                if (areas.Count == 0 && hiddenAreas.Count == 0)
                {
                    result?.Add(item);
                    continue;
                }
                result ??= [.. pages.Take(i)];
                _anyRedaction = true;
                if (areas.Count > 0) _bars = true;
                _cancel.ThrowIfCancellationRequested();
                var old = item.Page;
                var page = old;
                var removedText = "";
                IReadOnlySet<int>? mcids = null;
                if (hiddenAreas.Count > 0 && Redactor.RemoveHiddenIn(item.Source, old, hiddenAreas, _cancel) is { } cleaned)
                {
                    AddReplaced(item.Source, cleaned.ReplacedObjects);
                    AddTouched(item.Source, cleaned.TouchedFonts);
                    page = new PdfPage(old.Index, cleaned.Page, old.Id, cleaned.Resources, old.MediaBox, old.CropBox, old.Rotate);
                    removedText = cleaned.RemovedText;
                    mcids = cleaned.AlternateTextMcids;
                }
                else if (hiddenAreas.Count > 0)
                {
                    _idleHidden.Add(i);
                }
                var dropped = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                if (areas.Count > 0)
                {
                    // moved foreign annotations count at their new position (their pop-up moves with them)
                    var moved = new Dictionary<PdfDictionary, PdfRectangle>(ReferenceEqualityComparer.Instance);
                    foreach (var foreign in item.Elements.OfType<ForeignAnnotation>().Where(f => f.IsMoved))
                    {
                        moved[foreign.Original] = foreign.Rect;
                        if (ForeignAnnotations.PopupOf(foreign.Original) is { } popup && PdfRectangle.From(popup.Get(PdfName.Get("Rect"))) is { } p)
                            moved[popup] = new PdfRectangle(p.Left + foreign.Offset.X, p.Bottom + foreign.Offset.Y, p.Right + foreign.Offset.X, p.Top + foreign.Offset.Y);
                    }
                    var redaction = Redactor.Apply(item.Source, page, areas, _cancel, moved, hideLengths: _options.HideRedactedLengths);
                    if (redaction.Check is { } check) _writtenChecks.Add((result.Count, check));
                    if (redaction.LengthsPartlyShown > 0) _partlyShown.Add(i);
                    if (redaction.BarExtensions.Count > 0) _barExtensions[i] = redaction.BarExtensions;
                    AddReplaced(item.Source, redaction.ReplacedObjects);
                    AddTouched(item.Source, redaction.TouchedFonts);
                    Redactor.WithoutOldForms(redaction.Page);
                    page = new PdfPage(old.Index, redaction.Page, old.Id, redaction.Resources, old.MediaBox, old.CropBox, old.Rotate);
                    dropped.UnionWith(redaction.DroppedAnnotations);
                    removedText += redaction.RemovedText;
                    // characters the bars only grazed: those that nobody sees any more since the bar lies over what showed of them
                    if (redaction.GrazedGlyphs > 0 &&
                        Redactor.RemoveHiddenIn(item.Source, page, [.. areas, .. redaction.BarExtensions], _cancel, grazing: true) is { } grazed)
                    {
                        AddReplaced(item.Source, grazed.ReplacedObjects);
                        AddTouched(item.Source, grazed.TouchedFonts);
                        page = new PdfPage(old.Index, grazed.Page, old.Id, grazed.Resources, old.MediaBox, old.CropBox, old.Rotate);
                        removedText += grazed.RemovedText;
                    }
                }
                var rects = areas.Select(a => Graphics.RectD.FromLtrb(a.Left, a.Bottom, a.Right, a.Top)).ToList();
                var elements = item.Elements
                    .Where(e => e is not RedactionElement && !Redactor.Hits(rects, Graphics.RectD.FromLtrb(e.Rect.Left, e.Rect.Bottom, e.Rect.Right, e.Rect.Top)))
                    .ToList();
                // A bar makes the whole page count as redacted (all alternate texts in the structure go); hidden text alone takes
                // only those of the marked content it came from
                var redacted = item.WithRedactedPage(page, elements, areas.Count == 0 ? mcids : null);
                if (areas.Count > 0) _redacted.Add((redacted, old.Dictionary, dropped));
                // the page before, for bars within text (6.3 step 6, No. 659)
                if (areas.Count > 0 && ReferenceEquals(item.Source, _main) && old.Id is { } oldId) _barOriginals[oldId.Number] = old;
                _removedText[redacted] = removedText;
                _redactionDrops[page.Dictionary] = dropped;
                result.Add(redacted);
            }
            DropRepliesAcrossPages(result ?? pages);
            return result ?? pages;
        }

        /// <summary>
        /// Replies (/IRT, ISO 32000-2:2020, 12.5.6.2) and popups (/Parent, 12.5.6.14) of annotations dropped under a bar, on any page: a reply
        /// shows what it answers, a popup the note it belongs to. The bar's own page already handles its annotations (<see cref="Redactor"/>);
        /// this adds those of other pages, also replies to replies (at most 16 levels, as there).
        /// </summary>
        private void DropRepliesAcrossPages(IReadOnlyList<PageItem> pages)
        {
            var all = new HashSet<PdfDictionary>(_redactionDrops.Values.SelectMany(d => d), ReferenceEqualityComparer.Instance);
            if (all.Count == 0) return;
            var irt = PdfName.Get("IRT");
            bool changed;
            var rounds = 0;
            do
            {
                changed = false;
                foreach (var item in pages)
                {
                    if (item.Page.Dictionary.Get(Annots) is not PdfArray annots) continue;
                    for (var i = 0; i < Math.Min(annots.Count, 10_000); i++)
                    {
                        if (annots.Get(i) is not PdfDictionary annot || all.Contains(annot)) continue;
                        if (!(annot.Get(irt) is PdfDictionary answered && all.Contains(answered)
                              || annot.GetName(PdfName.Subtype)?.Value == "Popup" && annot.Get(PdfName.Parent) is PdfDictionary parent && all.Contains(parent)))
                            continue;
                        all.Add(annot);
                        if (!_redactionDrops.TryGetValue(item.Page.Dictionary, out var set))
                            _redactionDrops[item.Page.Dictionary] = set = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                        set.Add(annot);
                        changed = true;
                    }
                }
            } while (changed && ++rounds < 16);
        }

        private readonly Dictionary<PdfDocument, bool> _hasHiddenLayers = new(ReferenceEqualityComparer.Instance);

        // The pages of the scoped cleanup as written after the page passes (null: none scoped)
        private HashSet<PdfDictionary>? _scopedPages;

        /// <summary>The places that stay (<see cref="PdfSaveOptions.KeepPlaces"/>), by page index.</summary>
        private readonly Dictionary<int, List<CleanupKeep>> _keep = [];

        /// <summary>A test whether a place of the item on the page stays; null if none does.</summary>
        private Func<PdfRectangle, bool>? KeepFor(int page, CleanupItems item) =>
            _keep.TryGetValue(page, out var list) && list.Where(k => k.Item == item).ToList() is { Count: > 0 } mine ? r => mine.Any(k => k.Holds(r)) : null;

        /// <summary>The forms to put over pages (<see cref="PdfSaveOptions.TextLayers"/>, <see cref="PdfSaveOptions.Overlays"/>), by the dictionary of the page as written.</summary>
        private readonly Dictionary<PdfDictionary, List<PdfStream>> _overlays = new(ReferenceEqualityComparer.Instance);

        /// <summary>The annotations to add to pages (<see cref="PdfSaveOptions.AddedAnnotations"/>), by the dictionary of the page as written.</summary>
        private readonly Dictionary<PdfDictionary, IReadOnlyList<PdfDictionary>> _addedAnnotations = new(ReferenceEqualityComparer.Instance);

        /// <summary>The cleanup items of page <paramref name="index"/> (written order) in the page passes: the general ones and, on its pages, the scoped ones.</summary>
        private CleanupItems CleanupOf(int index) =>
            _options.ScopedCleanup is { } scoped && scoped.Pages.Contains(index) ? _options.Cleanup | scoped.Items & DocumentCleanup.PageItems : _options.Cleanup;

        /// <summary>The cleanup items of a page after the page passes (annotations, appearances).</summary>
        private CleanupItems CleanupOf(PageItem item) =>
            _scopedPages is not null && _options.ScopedCleanup is { } scoped && _scopedPages.Contains(item.Page.Dictionary)
                ? _options.Cleanup | scoped.Items & DocumentCleanup.PageItems : _options.Cleanup;

        /// <summary>
        /// Sanitises the document: removes the content of hidden layers, invisible text, unseen content and suspicious text
        /// from the pages, as selected (<see cref="Redactor.RemoveHidden"/>). Pages without such content stay as they are.
        /// </summary>
        private IReadOnlyList<PageItem> RemoveHiddenContent(IReadOnlyList<PageItem> pages)
        {
            List<PageItem>? result = null;
            for (var i = 0; i < pages.Count; i++)
            {
                var item = pages[i];
                if ((i & 15) == 0) _cancel.ThrowIfCancellationRequested();
                var items = CleanupOf(i);
                var layers = items.HasFlag(CleanupItems.HiddenLayers);
                var text = items.HasFlag(CleanupItems.InvisibleText);
                var outside = items.HasFlag(CleanupItems.HiddenContent);
                var suspicious = items.HasFlag(CleanupItems.SuspiciousText);
                // Without hidden layers in the file there is nothing to do for them
                var hiddenLayers = layers && (_hasHiddenLayers.TryGetValue(item.Source, out var known) ? known : _hasHiddenLayers[item.Source] = DocumentCleanup.HiddenLayerNames(item.Source).Count > 0);
                // a copy without layers: what the display hides goes (the file has layers at all)
                var flatten = FlattensLayers && item.Source.Catalog is { } layered && Present(layered, OCProperties);
                if (!hiddenLayers && !flatten && !text && !outside && !suspicious
                    || Redactor.RemoveHidden(item.Source, item.Page, hiddenLayers || flatten, text, _cancel, outside, suspicious, _keep.GetValueOrDefault(i),
                        screenLayers: flatten) is not { } cleaned)
                {
                    result?.Add(item);
                    continue;
                }
                AddReplaced(item.Source, cleaned.ReplacedObjects);
                AddTouched(item.Source, cleaned.TouchedFonts);
                result ??= [.. pages.Take(i)];
                var old = item.Page;
                var page = new PdfPage(old.Index, cleaned.Page, old.Id, cleaned.Resources, old.MediaBox, old.CropBox, old.Rotate);
                result.Add(item.WithRedactedPage(page, item.Elements, cleaned.AlternateTextMcids));
            }
            return result ?? pages;
        }

        /// <summary>
        /// „Verdeckte Teile von Bildern“: every image cut to what can be seen of it (<see cref="ContentObjectEditor.CutToVisible"/>). Pages with
        /// images that had to stay as they were (stencil masks, too large, unreadable) are reported (<see cref="PdfSaveResult.ImagePartsKept"/>).
        /// </summary>
        private IReadOnlyList<PageItem> CutHiddenImageParts(IReadOnlyList<PageItem> pages)
        {
            List<PageItem>? result = null;
            for (var i = 0; i < pages.Count; i++)
            {
                _cancel.ThrowIfCancellationRequested();
                var item = pages[i];
                if (!CleanupOf(i).HasFlag(CleanupItems.HiddenImageParts))
                {
                    result?.Add(item);
                    continue;
                }
                var keep = KeepFor(i, CleanupItems.HiddenImageParts);
                var (cut, done, skipped) = ContentObjectEditor.CutToVisible(item, DocumentCleanup.VisibleArea(item.Page), _options.ImageCodec, _cancel,
                    keep is null ? null : r => keep(new PdfRectangle(r.Left, r.Top, r.Right, r.Bottom)));
                if (skipped > 0) _imagePartsKept.Add(i);
                if (done == 0 && ReferenceEquals(cut, item))
                {
                    result?.Add(item);
                    continue;
                }
                result ??= [.. pages.Take(i)];
                result.Add(cut);
            }
            return result ?? pages;
        }

        private readonly List<int> _imagePartsKept = [];

        // Marks were applied on some page (bars or hidden content)
        private bool _anyRedaction;

        /// <summary>
        /// After redaction, the preview in the document's XMP (xmp:Thumbnails, an image of the first page) would still show the state before
        /// — like /Thumb of a page, which the redaction drops (docs/RECHERCHE-VERSTECKEN.md, B-4). Only the preview goes; the rest of the
        /// metadata stays as chosen.
        /// </summary>
        /// <summary>
        /// The title the copy gets (No. 492): the one given, or — cleaned up with „Titel behalten“ — the one of the file; null if it stays as
        /// it is.
        /// </summary>
        private string? WantedTitle()
        {
            if (_options.DocumentTitle is { } given) return given;
            if (!_options.Cleanup.HasFlag(CleanupItems.Metadata) || !_options.KeepTitle) return InheritedTitle();
            return _main.Trailer.GetDictionary(PdfName.Info)?.GetString(TitleKey)?.ToText() is { Length: > 0 } title ? title : null;
        }

        /// <summary>
        /// The tagged file a new file of merged files takes its title, language and „show the title“ from (docs/ABLAUFPLAN.md, 6.5 step 12;
        /// decided on 8.11.2026): the first in the order of the pages — where the main file gives no page (merged as a new file, the
        /// empty document underneath) and is untagged. Null if there is none.
        /// </summary>
        private PdfDocument? FirstTaggedSource()
        {
            if (_firstTaggedKnown) return _firstTagged;
            _firstTaggedKnown = true;
            if (Tagged() || _pages.Any(p => ReferenceEquals(p.Source, _main))) return null;
            return _firstTagged = _pages.Select(p => p.Source).FirstOrDefault(d => Accessibility.StructureRoles.IsTagged(d.Catalog));
        }

        private PdfDocument? _firstTagged;
        private bool _firstTaggedKnown;

        /// <summary>
        /// The title of <see cref="FirstTaggedSource"/> (/Title of its information dictionary, else dc:title of its XMP — a file of PDF 2.0 or
        /// one made for PDF/UA may have it only there) for a new file that has none (no title, no XMP); never when metadata are cleaned up
        /// (privacy: nothing of another file then). Null if it stays as it is.
        /// </summary>
        private string? InheritedTitle()
        {
            if (_options.Cleanup.HasFlag(CleanupItems.Metadata) || FirstTaggedSource() is not { } first || !string.IsNullOrEmpty(_main.Info.Title) ||
                _main.Catalog is { } described && Present(described, PdfName.Get("Metadata")))
                return null;
            if (first.Info.Title?.Trim() is { Length: > 0 } title) return title;
            try
            {
                return first.Catalog?.Get(PdfName.Get("Metadata")) is PdfStream { DataLength: <= 16 << 20 } xmp &&
                       XmpEditor.ReadInfo(first.GetStreamData(xmp))?.Title?.Trim() is { Length: > 0 } fromXmp
                    ? fromXmp
                    : null;
            }
            catch (PdfException)
            {
                return null;   // unreadable: no title taken
            }
        }

        /// <summary>The language of <see cref="FirstTaggedSource"/> as a valid tag (the one meant where it is clear, No. 638); null if none.</summary>
        private string? InheritedLanguage() =>
            FirstTaggedSource()?.Catalog?.GetString(LangKey)?.ToText().Trim() is { Length: > 0 } given
                ? Accessibility.LanguageTags.Valid(given) ? given : Accessibility.LanguageTags.Proposal(given)
                : null;

        /// <summary>„Show the title“ of <see cref="FirstTaggedSource"/> for a new file that says nothing about it; null if it stays as it is.</summary>
        private bool? InheritedDisplayDocTitle() =>
            FirstTaggedSource() is { } first && _main.Catalog?.GetDictionary(ViewerPreferencesKey)?.ContainsKey(PdfName.Get("DisplayDocTitle")) != true &&
            first.Catalog?.GetDictionary(ViewerPreferencesKey)?.GetBoolean(PdfName.Get("DisplayDocTitle")) == true
                ? true
                : null;

        /// <summary>A slim XMP packet for a copy whose title must go into the XMP but that has none (written with the catalog).</summary>
        private byte[]? _slimXmp;

        /// <summary>
        /// The title into the XMP (not for PDF/A: its packet is built from the information dictionary): an existing packet gets dc:title, a
        /// copy without one (or cleaned up) a slim packet.
        /// </summary>
        private void TitleIntoXmp()
        {
            if (_pdfA is not null || WantedTitle() is not { } title) return;
            if (!_options.Cleanup.HasFlag(CleanupItems.Metadata) && _main.Catalog?.GetRaw(PdfName.Get("Metadata")) is PdfReference reference &&
                _main.GetObject(reference.Id.Number) is PdfStream xmp && xmp.DataLength <= 16 << 20)
            {
                var source = AddSource(_main);
                try
                {
                    var current = source.Overrides.TryGetValue(reference.Id.Number, out var changed) && changed is PdfStream { RawData: { } cleaned } ? cleaned : _main.GetStreamData(xmp);
                    if (XmpEditor.SetTitle(current, title) is not { } withTitle) return;   // not well-formed: as it is
                    var dict = Clone(xmp.Dictionary);
                    dict.Remove(PdfName.Filter);
                    dict.Remove(PdfName.DecodeParms);
                    source.Overrides[reference.Id.Number] = new PdfStream(dict, withTitle);
                    return;
                }
                catch (PdfException)
                {
                    return;
                }
            }
            // cleaned up: the title alone, as in the information dictionary (no program, no date)
            if (title.Length == 0) return;   // cleared, and no packet to clear it in
            _slimXmp = _options.Cleanup.HasFlag(CleanupItems.Metadata) ? XmpEditor.Slim(title, null, null)
                : XmpEditor.Slim(title, Modified, Producer);
        }

        /// <summary>
        /// „Metadaten bereinigen“ while attached files described in the XMP stay (<see cref="PdfSaveOptions.AttachedFileMetadataStays"/>; an
        /// e-invoice not ticked, decision (4) of 15.11.2026): the slim packet keeps what they need — their properties with the extension
        /// schemas and the PDF/A identification — so that what stays, stays whole; the rest of the metadata goes as before.
        /// </summary>
        private void AttachedFileDataIntoSlimXmp()
        {
            if (_pdfA is not null || !_options.Cleanup.HasFlag(CleanupItems.Metadata) || !_options.AttachedFileMetadataStays ||
                AttachedFileMetadata.Namespaces is not { Count: > 0 } namespaces) return;
            if (_main.Catalog?.GetRaw(PdfName.Get("Metadata")) is not PdfReference reference || _main.GetObject(reference.Id.Number) is not PdfStream xmp ||
                xmp.DataLength > 16 << 20) return;
            try
            {
                if (PdfAXmp.Read(_main.GetStreamData(xmp)) is not { } xml || PdfAXmp.KeptWithAttachedFiles(xml, namespaces) is not { Length: > 0 } kept) return;
                _slimXmp = XmpEditor.Slim(WantedTitle() ?? "", null, null, kept);
            }
            catch (PdfException)
            {
                // unreadable: nothing to keep
            }
        }

        /// <summary>When the copy is written: once per save, the same in /ModDate and in the XMP.</summary>
        private DateTimeOffset Modified => _modified ??= _options.ModificationTime ?? DateTimeOffset.Now;

        private DateTimeOffset? _modified;

        private static string Producer => "PDF-Knead " + AppVersion.Display;

        /// <summary>The XMP of the source could not be read: the date of the change is not in it (said after saving).</summary>
        private bool _xmpNotUpdated;

        /// <summary>
        /// Every version (not PDF/A, which builds its packet itself, nor „Metadaten bereinigen“): the document's XMP gets the date of this
        /// change and the program, as /ModDate and /Producer of the information dictionary — both written, they must agree (ISO 32000-2, 14.3.4;
        /// readers of 2.0 take them from the XMP, 14.3.3; a PDF/A-1 file saved the ordinary way stays consistent, ISO 19005-1, 6.7.3), and a new
        /// instance identifier (XMP part 1, 8.6). Works on the packet as changed before (previews removed, title set): one override per save.
        /// Unreadable XMP stays as it is, and that is said (docs/ABLAUFPLAN.md, 5.3 d and 6.1; docs/FEHLERGEDAECHTNIS.md, No. 552 and 613).
        /// </summary>
        private void SavedIntoXmp()
        {
            if (_pdfA is not null || _options.Cleanup.HasFlag(CleanupItems.Metadata)) return;
            if (EditXmp(packet => XmpEditor.SetSaved(packet, Modified, Producer)) == false) _xmpNotUpdated = true;
        }

        /// <summary>
        /// Changes the document's XMP packet as it stands so far in this save (previews removed, title or dates set: one override per save).
        /// Null if there is none, false if it is too large or not well-formed (it stays as it is), true if changed.
        /// </summary>
        private bool? EditXmp(Func<byte[], byte[]?> edit)
        {
            if (_main.Catalog?.GetRaw(PdfName.Get("Metadata")) is not PdfReference reference || _main.GetObject(reference.Id.Number) is not PdfStream xmp) return null;
            if (xmp.DataLength > 16 << 20) return false;
            var source = AddSource(_main);
            try
            {
                var current = source.Overrides.TryGetValue(reference.Id.Number, out var changed) && changed is PdfStream { RawData: { } edited } ? edited : _main.GetStreamData(xmp);
                if (edit(current) is not { } result) return false;
                if (ReferenceEquals(result, current)) return true;   // nothing to change: no new packet
                var dict = Clone(xmp.Dictionary);
                dict.Remove(PdfName.Filter);
                dict.Remove(PdfName.DecodeParms);
                source.Overrides[reference.Id.Number] = new PdfStream(dict, result);
                return true;
            }
            catch (PdfException)
            {
                return false;
            }
        }

        /// <summary>
        /// The attachments of the document go in this save („Anhänge“ cleaned up, or a part without the attachments of the whole): the XMP
        /// properties that describe attached files (<see cref="AttachedFileMetadata"/>, registered by a module) go with them, with their
        /// extension schemas — the metadata must not name a file that is gone (decided on 15.11.2026; docs/FEHLERGEDAECHTNIS.md,
        /// No. 865). Not for PDF/A, which builds its packet itself, nor with „Metadaten bereinigen“, which slims the packet anyway.
        /// </summary>
        private void AttachedFileDataOff()
        {
            if (_pdfA is not null || _options.Cleanup.HasFlag(CleanupItems.Metadata) || _options.AttachedFileMetadataStays
                || AttachedFileMetadata.Namespaces is not { Count: > 0 } namespaces) return;
            if (!_options.Cleanup.HasFlag(CleanupItems.Attachments) && !(_options.PartOfDocument && !_options.KeepDocumentAttachments)) return;
            EditXmp(packet => XmpEditor.RemoveDescribed(packet, namespaces));
        }

        /// <summary>The embedded files kept despite „Anhänge“ (<see cref="PdfSaveOptions.KeptAttachments"/>): they belong to the main document.</summary>
        private IReadOnlySet<PdfObjectId>? KeptFor(PdfDocument doc) => ReferenceEquals(doc, _main) ? _options.KeptAttachments : null;

        private IReadOnlySet<PdfObjectId>? KeptFor(Source source) => KeptFor(source.Doc);

        /// <summary>Why the PDF/A identification of the source went (see <see cref="PdfAClaimOff"/>); null if it stayed or there was none.</summary>
        private string? _pdfAClaimRemovedFor;

        /// <summary>
        /// A source identified as PDF/A (pdfaid in its XMP) saved the ordinary way with an option that surely breaks PDF/A — encryption, Brotli,
        /// a version outside the part (part 1 above 1.4, parts 2 and 3 as 2.0, part 4 below 2.0): the identification is removed, it would be a
        /// false claim, and that is said after saving (ISO 19005, identification; docs/ABLAUFPLAN.md, 5.3 g; docs/FEHLERGEDAECHTNIS.md,
        /// No. 554). Likewise a digital signature of the source: the file is written anew, so its byte ranges no longer cover the file
        /// (ISO 19005-2 and -3, 6.4.3; phase 9.1, No. 709). Other breaks need a check after saving (open, OFFENE-PUNKTE).
        /// </summary>
        private void PdfAClaimOff()
        {
            if (_pdfA is not null || _options.Cleanup.HasFlag(CleanupItems.Metadata) || PdfACheck.Declared(_main) is not { } declared) return;
            var part = declared.Part();
            var reason = _security is not null ? Tr.T("Passwortschutz")
                : _options.Compression?.Brotli == true ? Tr.T("Brotli-Packverfahren")
                : part == 1 && CompareVersions(_version, "1.4") > 0 || part is 2 or 3 && Pdf20 || part == 4 && !Pdf20 ? Tr.T("PDF {0}", _version)
                : Signatures.DigitalSignatures.Find(_main).Count > 0 ? Tr.T("einer ungültig gewordenen digitalen Signatur")
                : null;
            if (reason is not null && EditXmp(packet => XmpEditor.RemoveNamespace(packet, XmpSchemas.PdfAId)) == true) _pdfAClaimRemovedFor = reason;
        }

        /// <summary>Bars were drawn on a page (not only hidden content removed).</summary>
        private bool _bars;

        /// <summary>What breaks the accessibility of a tagged file in this save (<see cref="CollectAccessibilityBreaks"/>).</summary>
        private readonly List<string> _accessibilityBreaks = [];

        /// <summary>The accessibility claims of the source went (<see cref="PdfUaClaimOff"/>).</summary>
        private bool _accessibilityClaimRemoved;

        /// <summary>
        /// Collects what breaks the accessibility of a tagged file (ISO 14289: all real content tagged, nothing untagged added) — before the
        /// XMP is decided on: bars, new content without structure (edited text, elements fixed into the page, text layers, flattened
        /// annotations), new annotations or fields without a structure element, pages of another file, the same page twice, „Bereinigen:
        /// Struktur“; and a PDF/UA-2 file written below 2.0, a PDF/UA-1 file as 2.0, tagged or not. Each reason goes when its solution is
        /// built in this phase (docs/ABLAUFPLAN.md, 6.1 step 3).
        /// </summary>
        private void CollectAccessibilityBreaks()
        {
            void Break(string reason)
            {
                if (!_accessibilityBreaks.Contains(reason)) _accessibilityBreaks.Add(reason);
            }
            int? uaPart = null;
            if (_main.Catalog?.GetRaw(PdfName.Get("Metadata")) is PdfReference metadata && _main.GetObject(metadata.Id.Number) is PdfStream xmp &&
                xmp.DataLength <= 16 << 20)
            {
                try
                {
                    // PDF/UA-2 needs PDF 2.0 (ISO 14289-2, 5); PDF/UA-1 allows PDF 1.0 to 1.7 (ISO 14289-1, 6.1; docs/FEHLERGEDAECHTNIS.md, No. 614)
                    var part = AccessibilityClaims.Read(_main.GetStreamData(xmp))?.PdfUaPart;
                    uaPart = part;
                    if (part >= 2 && !Pdf20) Break(Tr.T("PDF {0} (PDF/UA-2 setzt PDF 2.0 voraus)", _version));
                    if (part == 1 && Pdf20) Break(Tr.T("PDF {0} (PDF/UA-1 erlaubt höchstens PDF 1.7)", _version));
                }
                catch (PdfException)
                {
                }
            }
            // PDF/UA-1 tells the language of bookmarks and field tooltips by the catalogue's /Lang alone (ISO 14289-1, 7.2): bookmarks or fields
            // with a tooltip brought by an inserted file into a copy whose main file names none have no language (docs/FEHLERGEDAECHTNIS.md,
            // No. 680; the language of the inserted file is not set for all of it — its content may be in another)
            if (uaPart == 1 && _main.Catalog?.Get(PdfName.Get("Lang")) is not PdfString && _sourceOrder.Any(InsertsWithoutLanguage))
                Break(Tr.T("Lesezeichen oder Felder einer eingefügten Datei ohne Sprache (die Datei nennt keine)"));
            // the repair „Kennung entfernen“ (6.7 step 7): also for an untagged file claiming PDF/UA
            if (_options.RemoveAccessibilityClaim) Break(Tr.T("Kennung auf Wunsch entfernt"));
            if (!Tagged()) return;
            // bars alone break nothing any more (docs/ABLAUFPLAN.md, 6.3 step 7; docs/FEHLERGEDAECHTNIS.md, No. 660): they are artifacts (inline
            // ones in the tree, No. 659), emptied elements go (No. 649), descriptions are neutral (6.3 step 3) — what still breaks it after a
            // redaction is named by itself below (formula without MathML, private use characters, no tagged content left)
            // a formula on a redacted page loses its MathML (associated file, No. 444): for PDF/UA-2 often the only allowed way of showing it
            // (docs/ABLAUFPLAN.md, 6.3 step 4)
            if (_bars && uaPart >= 2 && FormulaWithSourceRedacted()) Break(Tr.T("Formel ohne MathML nach Schwärzen"));
            // characters of the private use areas left without a replacement text: the bars take every replacement text of their pages (No. 134,
            // design decision: privacy first); PDF/UA-2 asks for one (ISO 14289-2, 8.4.3), PDF/UA-1 allows them (ISO 14289-1, 7.2)
            // (docs/ABLAUFPLAN.md, 6.3 step 5)
            if (_bars && uaPart >= 2 && RedactedPrivateUse()) Break(Tr.T("Zeichen aus dem privaten Unicode-Bereich ohne Ersatztext nach Schwärzen"));
            if (_options.Cleanup.HasFlag(CleanupItems.Structure)) Break(Tr.T("Struktur bereinigt"));
            // PDF/A-1 knows the structure types of PDF 1.4 only: level a maps THead, TBody, TFoot and Annot to others (ISO 19005-1, 6.8.3.4), which
            // PDF/UA-1 forbids for its standard types (ISO 14289-1, 7.1; docs/FEHLERGEDAECHTNIS.md, No. 683) — before, the claim stayed
            if (uaPart == 1 && _pdfA is { } pdfA && pdfA.Part.Part() == 1 && pdfA.Part.Accessible() && !_options.Cleanup.HasFlag(CleanupItems.Structure) &&
                UnknownToPdfA1() is { Count: > 0 } unknown)
                Break(Tr.T("PDF/A-1 kennt {0} nicht (PDF/UA-1 verbietet, sie umzudeuten)", string.Join(", ", unknown)));
            // the pages left carry none of the tagged content: the copy is no longer tagged (No. 626)
            if (_structureGone) Break(Tr.T("kein getaggter Inhalt mehr"));
            // pages taken out left the headings beginning below H1 or skipping a level (not re-levelled: 6.6 h, No. 655)
            if (_headingOrderBroken) Break(Tr.T("Überschriftenfolge gebrochen"));
            // pages of another file break it only where that file's tree does not come along (MergeStructure, No. 638)
            // (an image standing for a page of the main file is no other file's page where it became a Figure: ImagePageFigures, No. 654)
            if (_pages.Select((p, i) => (p, i)).Any(e => !ReferenceEquals(e.p.Source, _main) && _sources[e.p.Source].StructKeyOffset is null &&
                                                        !_imagePageMarks.ContainsKey(_pageNumbers[e.i])))
                Break(Tr.T("Seiten aus einer anderen Datei"));
            // a page taken more than once breaks it only where a further copy got no structure of its own (DuplicatePageStructure, No. 642)
            if (CopiesWithoutStructure()) Break(Tr.T("dieselbe Seite mehrfach"));
            foreach (var item in _pages)
            {
                if (!ReferenceEquals(item.Source, _main)) continue;
                var own = OwnElements(item);
                // forms drawn over the page are new content, unless all they paint is an artifact (the reasons in bars, 6.3 step 2) or they are a
                // text layer of the text recognition in a copy that keeps the tree (a paragraph, No. 657)
                // fixed elements get their structure in a copy that keeps the tree (FixedIntoContent, No. 634), edited text too (No. 656)
                if (own.Any(e => !Annotated(e) && Printed(e) && !KeepsStructure()) ||
                    _overlays.TryGetValue(item.Page.Dictionary, out var drawnOver) && drawnOver.Any(f => !(_textLayerForms.Contains(f) && KeepsStructure()) &&
                        (f.Dictionary.ContainsKey(PdfName.Filter) || f.RawData is not { } data || !Accessibility.StructureRoles.OnlyArtifacts(data))))
                    Break(Tr.T("neuer Inhalt ohne Struktur"));
                // a copy that keeps the tree gives new annotations and fields an element of their own (TagNewAnnotation, No. 633)
                if ((own.Any(Annotated) || _addedAnnotations.ContainsKey(item.Page.Dictionary)) && !KeepsStructure())
                    Break(Tr.T("neue Anmerkungen oder Felder ohne Struktur"));
                // flattening a copy that keeps the tree gives every flattened annotation its structure (marked content of its element, an
                // element of its own or an artifact; FlattenedIntoContent, No. 632); without one it leaves untagged content
                if (_options.Flatten && item.Page.Dictionary.Get(Annots) is PdfArray { Count: > 0 } && !KeepsStructure()) Break(Tr.T("Flachlegen"));
            }
        }

        /// <summary>
        /// The standard types of PDF 1.7 that are none in PDF 1.4 (THead, TBody, TFoot, Annot) which the main file's tree uses or its role map
        /// leads to — PDF/A-1 level a maps them to others (No. 683). Sorted.
        /// </summary>
        private List<string> UnknownToPdfA1()
        {
            if (_main.Catalog?.GetDictionary(StructTreeRoot) is not { } root) return [];
            var types = StructureTypes(root);
            if (root.Get(PdfName.Get("RoleMap")) is PdfDictionary roles)
                foreach (var (key, _) in roles.Entries.Take(10_000))
                    if (roles.Get(key) is PdfName value) types.Add(value.Value);
            return [.. types.Where(t => Accessibility.StandardStructure.IsStandard(t, Accessibility.StructureSpace.Pdf17) && !PdfACheck.StandardStructureType(t, 1))
                .Order(StringComparer.Ordinal)];
        }

        /// <summary>An inserted file whose pages come along with its bookmarks or with fields that have a tooltip (/TU) (No. 680).</summary>
        private bool InsertsWithoutLanguage(Source source)
        {
            if (source.IsMain || source.Items.Count == 0) return false;
            if (source.Doc.Catalog?.GetDictionary(PdfName.Outlines)?.Get(PdfName.First) is PdfDictionary && _options.OutlinesLeftOut?.Contains(source.Doc) != true &&
                !_options.Cleanup.HasFlag(CleanupItems.Bookmarks))
                return true;
            foreach (var item in source.Items.Take(10_000))
                if (item.Page.Dictionary.Get(Annots) is PdfArray annots)
                    for (var i = 0; i < annots.Count && i < 10_000; i++)
                        if (annots.Get(i) is PdfDictionary annot && annot.GetName(PdfName.Subtype)?.Value == "Widget" && Tooltip(annot) is not null)
                            return true;
            return false;

            static PdfObject? Tooltip(PdfDictionary? field)
            {
                for (var level = 0; level < 32 && field is not null; level++, field = field.Get(PdfName.Get("Parent")) as PdfDictionary)
                    if (field.Get(PdfName.Get("TU")) is PdfString tu) return tu;
                return null;
            }
        }

        /// <summary>
        /// Whether what the save adds or flattens gets its structure: the copy keeps the tree of the main file, and its root is an object of its
        /// own (<see cref="StructureEdits"/>).
        /// </summary>
        private bool KeepsStructure() => TaggedCopy() && _main.Catalog?.GetRaw(StructTreeRoot) is PdfReference;

        /// <summary>
        /// A save that broke accessibility (<see cref="_accessibilityBreaks"/>) removes the source's claims of it from the XMP — the PDF/UA
        /// identification and the accessibility declarations; others stay. Not for PDF/A (its packet is built anew) nor „Metadaten bereinigen“
        /// (no XMP left). ISO 14289-1 and -2, clause 5; docs/ABLAUFPLAN.md, 6.1 steps 4–6.
        /// </summary>
        private void PdfUaClaimOff()
        {
            if (_accessibilityBreaks.Count == 0 || _pdfA is not null || _options.Cleanup.HasFlag(CleanupItems.Metadata)) return;
            var removed = false;
            if (EditXmp(packet => AccessibilityClaims.Remove(packet, out removed)) == true && removed) _accessibilityClaimRemoved = true;
        }

        /// <summary>
        /// The entries of the information dictionary the XMP has as well, brought in line (XMP part 3, 2.2.2): if /ModDate of the source is more
        /// recent than xmp:MetadataDate (or the packet has no metadata date while /ModDate is there), the entries of the information dictionary are
        /// in force and go into the XMP; otherwise — no /ModDate either — the XMP values are, and go into the information dictionary (from 2.0 only into entries it still has: the dictionary
        /// is deprecated there, ISO 32000-2, 14.3.3). A title given for the copy is set by itself. Not for
        /// „Metadaten bereinigen“; an unreadable packet stays as it is. PDF/A builds its packet from the dictionary: only the XMP values in force go
        /// into it (docs/ABLAUFPLAN.md, 6.1 step 2 a and 7; docs/FEHLERGEDAECHTNIS.md, No. 613 and 614).
        /// </summary>
        private void ReconcileInfo()
        {
            if (_options.Cleanup.HasFlag(CleanupItems.Metadata)) return;
            if (_main.Catalog?.GetRaw(PdfName.Get("Metadata")) is not PdfReference reference || _main.GetObject(reference.Id.Number) is not PdfStream xmp ||
                xmp.DataLength > 16 << 20) return;
            XmpEditor.InfoValues? found;
            try
            {
                found = XmpEditor.ReadInfo(_main.GetStreamData(xmp));
            }
            catch (PdfException)
            {
                return;
            }
            if (found is null) return;
            var info = _main.Trailer.GetDictionary(PdfName.Info);
            var infoDate = Document.PdfDate.Parse(info?.GetString(ModDateKey)?.ToText());
            var xmpInForce = !(infoDate is { } changed && (found.MetadataDate is not { } metadataDate || changed > metadataDate));
            var keys = XmpEditor.InfoKeys.Where(k => k != "Title" || WantedTitle() is null);
            if (xmpInForce)
            {
                var values = new Dictionary<string, string>();
                foreach (var key in keys)
                    if (found.Of(key) is { Length: > 0 } value && info?.GetString(PdfName.Get(key))?.ToText() != value &&
                        (!Pdf20 || _pdfA is not null || info?.ContainsKey(PdfName.Get(key)) == true))
                        values[key] = value;
                if (values.Count > 0) _infoFromXmp = values;
                return;
            }
            if (_pdfA is not null) return;   // its packet is built from the information dictionary
            var fromInfo = keys.Select(k => (Key: k, Value: info!.GetString(PdfName.Get(k))?.ToText())).Where(e => !string.IsNullOrWhiteSpace(e.Value))
                .Select(e => (e.Key, e.Value!)).ToList();
            if (fromInfo.Count > 0) EditXmp(packet => XmpEditor.SetInfo(packet, fromInfo));
        }

        /// <summary>Entries of the information dictionary taken from the XMP, which is in force (<see cref="ReconcileInfo"/>); null if none.</summary>
        private Dictionary<string, string>? _infoFromXmp;

        /// <summary>
        /// The preview (and for a part of the document the identifiers of the whole) out of the XMP, read as XML; the regular expressions only for
        /// a packet that is not well-formed (docs/ABLAUFPLAN.md, 6.1 step 2 e).
        /// </summary>
        private void RemoveXmpThumbnails()
        {
            if (EditXmp(packet => XmpEditor.RemoveThumbnails(packet, _options.PartOfDocument)) != false) return;
            if (_main.Catalog?.GetRaw(PdfName.Get("Metadata")) is not PdfReference reference || _main.GetObject(reference.Id.Number) is not PdfStream xmp ||
                xmp.DataLength > 16 << 20) return;
            byte[] data;
            try
            {
                data = _main.GetStreamData(xmp);
            }
            catch (PdfException)
            {
                return;
            }
            var text = System.Text.Encoding.UTF8.GetString(data);
            var cleaned = XmpThumbnails().Replace(text, "");
            // a part of the document is a resource of its own (XMP part 1, 6.1): without the history and the identifiers of the whole
            if (_options.PartOfDocument) cleaned = XmpOfTheWhole().Replace(cleaned, "");
            if (cleaned.Length == text.Length) return;
            var dict = Clone(xmp.Dictionary);
            dict.Remove(PdfName.Filter);
            dict.Remove(PdfName.DecodeParms);
            AddSource(_main).Overrides[reference.Id.Number] = new PdfStream(dict, System.Text.Encoding.UTF8.GetBytes(cleaned));
        }

        /// <summary>The preview element of XMP (any prefix, usually xmp: or xap:), with its content or empty; also a lone image of the
        /// image namespace (xmpGImg:image) outside it.</summary>
        [System.Text.RegularExpressions.GeneratedRegex(@"<(\w+):Thumbnails\b[^>]*?(?:/>|>.*?</\1:Thumbnails\s*>)|<xmpGImg:image\b[^>]*>.*?</xmpGImg:image\s*>",
            System.Text.RegularExpressions.RegexOptions.Singleline)]
        private static partial System.Text.RegularExpressions.Regex XmpThumbnails();

        /// <summary>
        /// What in XMP describes the whole document and its history (xmpMM: identifiers, history, derivation, ingredients, versions,
        /// manifest), as an element with its content, empty, or as an attribute.
        /// </summary>
        [System.Text.RegularExpressions.GeneratedRegex(
            @"<xmpMM:(DocumentID|InstanceID|OriginalDocumentID|VersionID|History|DerivedFrom|Ingredients|Versions|Manifest|RenditionOf|ManagedFrom)\b[^>]*?(?:/>|>.*?</xmpMM:\1\s*>)" +
            @"|\s+xmpMM:(?:DocumentID|InstanceID|OriginalDocumentID|VersionID)\s*=\s*(?:""[^""]*""|'[^']*')",
            System.Text.RegularExpressions.RegexOptions.Singleline)]
        private static partial System.Text.RegularExpressions.Regex XmpOfTheWhole();

        /// <summary>
        /// „Unsichtbarer Text“ also in the appearance streams of the annotations (/AP): OCR programs do not put their text layer there,
        /// but a crafted file can hide text in it. Each affected stream is rewritten like a page (<see cref="Redactor.RemoveHidden"/>)
        /// and takes the place of the original object, so every annotation sharing it gets the new one; what it looks like stays.
        /// </summary>
        private void RemoveInvisibleTextFromAppearances()
        {
            var done = new Dictionary<PdfDocument, HashSet<int>>(ReferenceEqualityComparer.Instance);
            foreach (var item in _pages)
            {
                _cancel.ThrowIfCancellationRequested();
                if (!CleanupOf(item).HasFlag(CleanupItems.InvisibleText)) continue;
                if (!done.TryGetValue(item.Source, out var numbers)) done[item.Source] = numbers = [];
                foreach (var (number, stream) in DocumentCleanup.InvisibleTextAppearances(item.Source, item.Page))
                {
                    if (!numbers.Add(number)) continue;
                    // The stream as the content of a page of its own: its resources, its box
                    var form = DocumentCleanup.AsPage(item.Page, stream);
                    if (Redactor.RemoveHidden(item.Source, form, layers: false, invisibleText: true, _cancel) is not { } cleaned
                        || cleaned.Page.Get(PdfName.Contents) is not PdfStream { RawData: { } body }) continue;
                    var look = Clone(stream.Dictionary);
                    look.Remove(PdfName.Filter);
                    look.Remove(PdfName.DecodeParms);
                    look.Set(PdfName.Resources, cleaned.Resources);
                    AddSource(item.Source).Overrides[number] = new PdfStream(look, body);
                    AddReplaced(item.Source, cleaned.ReplacedObjects);
                    AddTouched(item.Source, cleaned.TouchedFonts);
                }
            }
        }

        /// <summary>
        /// Removes annotations under redactions from the file entirely, including their pop-ups and replies; references to them
        /// (structure, other annotations) become null. Widgets disappear from their field; a field without widgets is dropped.
        /// </summary>
        private void RemoveRedactedAnnotations(Source source, int? page, PdfDictionary original, HashSet<PdfDictionary> dropped)
        {
            if (page is { } id) source.RedactedPages.Add(id);
            if (original.Get(Annots) is not PdfArray annots) return;
            for (var i = 0; i < annots.Count; i++)
                if (annots[i] is PdfReference reference && annots.Get(i) is PdfDictionary a && dropped.Contains(a)) source.Removed.Add(reference.Id.Number);
            // what stays: every description of the redacted page goes, also in the appearances of its annotations — a copy of the annotation
            // with copies of those streams (shared ones stay as they are for the others; docs/FEHLERGEDAECHTNIS.md, No. 697)
            for (var i = 0; i < annots.Count; i++)
            {
                if (annots[i] is not PdfReference kept || annots.Get(i) is not PdfDictionary annotation || dropped.Contains(annotation)) continue;
                var current = source.Overrides.TryGetValue(kept.Id.Number, out var o) && o is PdfDictionary od ? od : annotation;
                if (current.GetDictionary(PdfName.Get("AP")) is not { } appearances ||
                    Redactor.AppearancesWithoutDescriptions(source.Doc, appearances) is not { } plain) continue;
                // the new streams as objects of their own (an override is written as it is; a stream inside it would lose its data)
                var written = new PdfDictionary(plain.Count);
                foreach (var (key, value) in plain.Entries)
                    written.Set(key, value switch
                    {
                        PdfStream { Id: null } stream => Sourced(stream, source),
                        PdfDictionary states => StatesSourced(states),
                        _ => value,
                    });
                var copy = Clone(current);
                copy.Set(PdfName.Get("AP"), written);
                source.Overrides[kept.Id.Number] = copy;
            }

            PdfDictionary StatesSourced(PdfDictionary states)
            {
                var result = new PdfDictionary(states.Count);
                foreach (var (key, value) in states.Entries) result.Set(key, value is PdfStream { Id: null } stream ? Sourced(stream, source) : value);
                return result;
            }

            // Fields: remove the widget from the field's /Kids, and fields that become empty, upwards
            foreach (var widget in dropped.ToList())
            {
                var child = widget;
                for (var depth = 0; depth < 32 && child.GetRaw(PdfName.Parent) is PdfReference parentRef &&
                                    source.Doc.GetObject(parentRef.Id.Number) is PdfDictionary parent; depth++)
                {
                    var current = source.Overrides.TryGetValue(parentRef.Id.Number, out var o) && o is PdfDictionary od ? od : parent;
                    if (current.Get(PdfName.Kids) is not PdfArray kids) break;
                    var kept = new PdfArray();
                    for (var k = 0; k < kids.Count; k++)
                        if (!(kids[k] is PdfReference kid && source.Removed.Contains(kid.Id.Number)) && !(kids.Get(k) is PdfDictionary kd && dropped.Contains(kd)))
                            kept.Add(kids[k]);
                    if (kept.Count > 0)
                    {
                        var copy = Clone(current);
                        copy.Set(PdfName.Kids, kept);
                        source.Overrides[parentRef.Id.Number] = copy;
                        break;
                    }
                    source.Removed.Add(parentRef.Id.Number);
                    dropped.Add(parent);
                    child = parent;
                }
            }
        }

        // Foreign annotations per page: moved ones (annotation → copy with the new position; Copy: as a separate object for
        // this page only) and deleted ones (with pop-up and replies). Per page, not per page dictionary: the same page can be
        // taken over twice, each copy with its own changes.
        private readonly Dictionary<PageItem, Dictionary<PdfDictionary, (PdfDictionary Moved, bool Copy)>> _foreignMoved = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<PageItem, HashSet<PdfDictionary>> _foreignDeleted = new(ReferenceEqualityComparer.Instance);
        private Dictionary<(PdfDocument, int), int>? _annotationUses;

        /// <summary>How often the object appears in /Annots of the new pages (page taken over twice, shared annotation).</summary>
        private int AnnotationUses(PdfDocument doc, int number)
        {
            if (_annotationUses is null)
            {
                _annotationUses = [];
                foreach (var page in _pages)
                    if (page.Page.Dictionary.Get(Annots) is PdfArray list)
                        for (var i = 0; i < list.Count; i++)
                            if (list[i] is PdfReference r)
                                _annotationUses[(page.Source, r.Id.Number)] = _annotationUses.GetValueOrDefault((page.Source, r.Id.Number)) + 1;
            }
            return _annotationUses.GetValueOrDefault((doc, number));
        }

        /// <summary>
        /// Foreign annotations of a page that were moved or deleted as elements. If one is only on this page, the moved one
        /// replaces the object (all references see the new position, the pop-up moves with it), and a deleted one is removed
        /// from the file entirely, widgets also from their field, as under redactions. If it is also on another page, only
        /// this page gets a moved copy or loses the entry.
        /// </summary>
        private void ForeignChanges(PageItem item)
        {
            var originals = ForeignAnnotations.For(item.Source, item.Page);
            if (originals.Count == 0 || item.Page.Dictionary.Get(Annots) is not PdfArray annots) return;
            var source = _sources[item.Source];
            var current = new Dictionary<PdfDictionary, ForeignAnnotation>(ReferenceEqualityComparer.Instance);
            foreach (var element in item.Elements)
                if (element is ForeignAnnotation foreign) current[foreign.Original] = foreign;
            Dictionary<PdfDictionary, int>? numbers = null;   // object numbers of the entries (once per page)
            var deleted = new List<PdfDictionary>();
            foreach (var original in originals)
            {
                if (!current.TryGetValue(original.Original, out var now))
                {
                    deleted.Add(original.Original);
                    continue;
                }
                if (!now.IsChanged && !_movedWidgets.ContainsKey(original.Original)) continue;
                var (dx, dy) = now.Offset;
                var number = Number(original.Original);
                var alone = number is { } n && AnnotationUses(item.Source, n) == 1;
                var copy = ForeignAnnotations.Moved(original.Original, dx, dy);   // a copy, even if not moved
                if (FormField.Of(now) is { } field)
                {
                    // Properties changed: entries and new appearance; check boxes get their state anew (the on state is
                    // named after the export value, which may have changed)
                    if (now.Spec is { } spec) ApplySpec(copy, now, field, spec, item.Source, number);
                    var value = now.Value;
                    if (now.Spec is { Kind: FormFieldKind.CheckBox or FormFieldKind.Radio } checkSpec && ElementAppearance.NeedsNewLook(now))
                        value = (value ?? field.State) == "Off" ? "Off" : ElementAppearance.OnStateOf(checkSpec);
                    if (value is not null) Fill(copy, now, field, value, item.Source);
                    Relink(copy, now, field, number);   // linked fields: to another field, or a field on its own
                }
                Replace(original.Original, copy, number, alone);
                // The pop-up moves only if the annotation object itself is replaced (it points to it with /Parent)
                if (now.IsMoved && alone && ForeignAnnotations.PopupOf(original.Original) is { } popup &&
                    (ForeignAnnotations.PopupReference(original.Original)?.Id.Number ?? Number(popup)) is { } p && AnnotationUses(item.Source, p) <= 1)
                    Replace(popup, ForeignAnnotations.Moved(popup, dx, dy), p, true);
            }
            if (deleted.Count == 0) return;
            var all = ForeignAnnotations.WithCompanions(annots, deleted);
            _foreignDeleted[item] = all;
            // Removed from the file entirely only if no other page shows it any more
            var gone = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            foreach (var annot in all)
                if (Number(annot) is not { } n || AnnotationUses(item.Source, n) <= 1) gone.Add(annot);
            if (gone.Count > 0) RemoveRedactedAnnotations(source, null, item.Page.Dictionary, gone);
            _foreignRemoved.UnionWith(gone);

            int? Number(PdfDictionary annot)
            {
                if (numbers is null)
                {
                    numbers = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
                    for (var i = 0; i < annots.Count; i++)
                        if (annots[i] is PdfReference r && annots.Get(i) is PdfDictionary d) numbers.TryAdd(d, r.Id.Number);
                }
                return numbers.TryGetValue(annot, out var n) ? n : null;
            }

            void Replace(PdfDictionary annot, PdfDictionary changed, int? number, bool alone)
            {
                if (!_foreignMoved.TryGetValue(item, out var map))
                    _foreignMoved[item] = map = new Dictionary<PdfDictionary, (PdfDictionary, bool)>(ReferenceEqualityComparer.Instance);
                map[annot] = (changed, number is not null && !alone);
                if (alone) source.Overrides[number!.Value] = changed;   // all references see the new position
            }
        }

        // Deleted foreign annotations that are removed from the file entirely (references to them become null)
        private readonly HashSet<PdfDictionary> _foreignRemoved = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Sanitising: annotations and form fields that nobody sees and that nothing can show
        /// (<see cref="HiddenContent.Annotations"/>) are removed from the file entirely, with their pop-ups, like deleted
        /// foreign annotations: from the field tree and /Fields; references to them become null. What an action or a script
        /// can show stays.
        /// </summary>
        private void DropUnseenAnnotations(PageItem item, Func<PdfRectangle, bool>? keep)
        {
            if (item.Page.Dictionary.Get(Annots) is not PdfArray annots) return;
            var scan = HiddenContent.Annotations(item.Source, item.Page, _options.Cleanup.HasFlag(CleanupItems.Scripts));
            // an annotation the user keeps stays with its companions (its place is its rectangle)
            var unseen = keep is null ? scan.Unseen : [.. scan.Unseen.Where(a => Content.AnnotationAppearance.RectOf(a) is not { } r || !keep(r))];
            if (unseen.Count == 0) return;
            var all = ForeignAnnotations.WithCompanions(annots, unseen);
            if (_foreignDeleted.TryGetValue(item, out var earlier)) all.UnionWith(earlier);
            _foreignDeleted[item] = all;
            var numbers = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < annots.Count; i++)
                if (annots[i] is PdfReference r && annots.Get(i) is PdfDictionary d) numbers.TryAdd(d, r.Id.Number);
            // Removed from the file entirely only if no other page shows it any more
            var gone = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            foreach (var annot in all)
                if (!numbers.TryGetValue(annot, out var n) || AnnotationUses(item.Source, n) <= 1) gone.Add(annot);
            if (gone.Count > 0) RemoveRedactedAnnotations(_sources[item.Source], null, item.Page.Dictionary, gone);
            _foreignRemoved.UnionWith(gone);
        }

        // Filled-in fields: text and choice fields with a new appearance (the copy receives it after PrepareElements, once the
        // font subsets are fixed), new values of terminal fields that are not also widgets (via their object number)
        private readonly List<(PdfDictionary Widget, ElementAppearance Appearance)> _filledForms = [];
        private readonly Dictionary<PdfDictionary, ElementAppearance> _filledText = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<PdfDictionary> _filledWidgets = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<PdfDictionary, (Source Source, int Number, PdfObject Value, bool Choice)> _fieldValues = new(ReferenceEqualityComparer.Instance);
        private static readonly PdfName AS = PdfName.Get("AS"), V = PdfName.Get("V"), I = PdfName.Get("I");

        /// <summary>
        /// Writes a filled-in field into the copy of its widget: text and choice fields get a new appearance and the value, check
        /// boxes and radio buttons the state (/AS); the value of the field (/V) is set on the terminal field, or in the widget
        /// itself for a merged field and widget.
        /// </summary>
        private void Fill(PdfDictionary copy, ForeignAnnotation element, FormField field, string value, PdfDocument doc)
        {
            _filledWidgets.Add(element.Original);
            // a check box, radio button or push button without an appearance (settled for PDF 2.0 or PDF/A): the one the display draws — the
            // states with frame and symbol, the caption; before, only /AS was set and the copy got an empty appearance (docs/FEHLERGEDAECHTNIS.md,
            // No. 889). A push button's caption comes with the font subsets, as filled-in text does.
            if (field.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio or FormFieldKind.PushButton &&
                FieldAppearance.MissingFieldValue(element.Original) is not null && !ElementAppearance.NeedsNewLook(element))
            {
                var look = ElementAppearance.Final(element);
                if (look.States is { } lookStates)
                {
                    var normal = new PdfDictionary();
                    foreach (var (name, form) in lookStates) normal.Set(PdfName.Get(name), Fresh(form));
                    var states = new PdfDictionary();
                    states.Set(PdfName.N, normal);
                    copy.Set(AP, states);
                }
                else _filledForms.Add((copy, look));
                if (field.Kind == FormFieldKind.PushButton) return;
            }
            PdfObject fieldValue;
            var choice = field.Kind is FormFieldKind.ComboBox or FormFieldKind.ListBox;
            switch (field.Kind)
            {
                case FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.ListBox:
                    var appearance = ElementAppearance.Final(element);
                    if (!ElementAppearance.NeedsNewLook(element)) _filledForms.Add((copy, appearance));   // otherwise already done in ApplySpec
                    _filledText[element.Original] = appearance;
                    copy.Remove(AS);
                    // List box with multiple selection: several values as an array (12.7.5.4)
                    fieldValue = (element.Spec?.MultiSelect ?? field.MultiSelect) && value.Contains('\n')
                        ? new PdfArray([.. value.Split('\n').Take(10_000).Select(v => (PdfObject)new PdfString(PdfTextEncoding.Encode(v)))])
                        : new PdfString(PdfTextEncoding.Encode(value));
                    break;
                case FormFieldKind.CheckBox or FormFieldKind.Radio:
                    copy.Set(AS, PdfName.Get(value));
                    // settled with the state it shows: only the appearance (and /AS, required with states) — the value stays as read, also a
                    // stray /V in the widget (docs/FEHLERGEDAECHTNIS.md, No. 916)
                    if (_settledStates.Contains(element.Original)) return;
                    // some programs (incorrectly) also put the value into a widget that is no field of its own: it would hide the value of
                    // the field — also in widgets that are off after the one that is on (docs/FEHLERGEDAECHTNIS.md, No. 343)
                    if (!ReferenceEquals(field.Field, field.Widget)) copy.Remove(V);
                    // Radio group: the widget that is on determines the value; widgets that are off do not overwrite it
                    if (value == "Off" && _fieldValues.ContainsKey(field.Field)) return;
                    fieldValue = PdfName.Get(value);
                    break;
                default:
                    return;
            }
            if (ReferenceEquals(field.Field, field.Widget))
            {
                SetValue(copy, fieldValue);
                if (choice) copy.Remove(I);
            }
            else
            {
                // (a widget leaving its field: the value belongs to the field it goes to, Relink)
                if (field.Widget.GetRaw(PdfName.Parent) is PdfReference parent && !_movedWidgets.ContainsKey(element.Original))
                    _fieldValues[field.Field] = (_sources[doc], parent.Id.Number, fieldValue, choice);
                // Some programs (incorrectly) also put the value into the widget; it would otherwise hide the new value on the field
                copy.Remove(V);
                copy.Remove(RV);
                if (choice) copy.Remove(I);
            }
        }

        // Changed properties: field entries of terminal fields that are not also widgets; calculated fields for /CO
        private readonly Dictionary<PdfDictionary, (Source Source, int Number, FieldSpec Spec, FieldSpec? Before)> _fieldSpecs = new(ReferenceEqualityComparer.Instance);
        // fields of the file whose calculation was set or changed here (with their full name and calculation, for the order in /CO)
        private readonly List<(Source Source, int Number, string Name, FieldCalculation Calc)> _calculatedForeign = [];
        private bool _specChanged;
        private static readonly PdfName AP = PdfName.Get("AP"), RectKey = PdfName.Get("Rect");

        /// <summary>
        /// Writes changed properties into the copy of a foreign widget: widget entries, field entries (in the widget for a merged
        /// field, otherwise on the terminal field via <see cref="ApplyFieldValues"/>), a new appearance (check boxes with their
        /// states; text only after <see cref="PrepareElements"/>, with the font as a subset). The form is then assembled anew
        /// (without XFA).
        /// </summary>
        private void ApplySpec(PdfDictionary copy, ForeignAnnotation element, FormField field, FieldSpec spec, PdfDocument doc, int? number)
        {
            _specChanged = true;
            _filledWidgets.Add(element.Original);
            if (element.IsResized)
            {
                var r = element.Rect;
                copy.Set(RectKey, new PdfArray([new PdfReal(Math.Round(r.Left, 3)), new PdfReal(Math.Round(r.Bottom, 3)),
                    new PdfReal(Math.Round(r.Right, 3)), new PdfReal(Math.Round(r.Top, 3))]));
            }
            // Appearance unchanged (only name, required flag …): the other program's appearance stays
            var look = ElementAppearance.NeedsNewLook(element);
            if (look) spec.WriteWidget(copy, ElementAppearance.WidgetRotation(element.Original));
            else spec.WriteVisibility(copy);
            int? fieldNumber = null;
            var before = FieldSpec.Of(element);   // as in the file: scripts are rewritten only if format or calculation changed
            if (ReferenceEquals(field.Field, field.Widget))
            {
                spec.WriteField(copy, before);
                fieldNumber = number;
            }
            else if (field.Widget.GetRaw(PdfName.Parent) is PdfReference parent && !_movedWidgets.ContainsKey(element.Original))
            {
                _fieldSpecs[field.Field] = (_sources[doc], parent.Id.Number, spec, before);
                fieldNumber = parent.Id.Number;
            }
            if (spec.Kind == FormFieldKind.Text && spec.Calculation is { IsActive: true } calc && fieldNumber is { } calculated && !Equals(calc, before?.Calculation))
                _calculatedForeign.Add((_sources[doc], calculated, spec.Name.Length > 0 ? spec.Name : field.Name, calc));
            if (!look) return;
            var appearance = ElementAppearance.Final(element);
            _filledText[element.Original] = appearance;   // flattening: the new appearance
            if (appearance.States is { } states)
            {
                var normal = new PdfDictionary();
                foreach (var (name, form) in states) normal.Set(PdfName.Get(name), Fresh(form));
                var ap = new PdfDictionary();
                ap.Set(PdfName.N, normal);
                copy.Set(AP, ap);
            }
            else if (element.Value is null || spec.Kind is not (FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.ListBox))
                _filledForms.Add((copy, appearance));
        }

        /// <summary>Writes new values into the terminal fields (as a copy, also on top of a change already made by redaction).</summary>
        private void ApplyFieldValues()
        {
            // First the properties, then the values on top
            foreach (var (field, (source, number, spec, before)) in _fieldSpecs)
            {
                if (source.Overrides.TryGetValue(number, out var o) && o is PdfDictionary own) spec.WriteField(own, before);
                else
                {
                    var copy = Clone(field);
                    spec.WriteField(copy, before);
                    source.Overrides[number] = copy;
                }
            }
            foreach (var (field, (source, number, value, choice)) in _fieldValues)
            {
                // Already a copy of its own (the field is also a filled-in widget, or redaction changed it): write into it; a new
                // copy would otherwise replace the widget together with its new appearance, which is added only afterwards
                if (source.Overrides.TryGetValue(number, out var o) && o is PdfDictionary own)
                {
                    SetValue(own, value);
                    if (choice) own.Remove(I);
                    continue;
                }
                var copy = Clone(field);
                SetValue(copy, value);
                if (choice) copy.Remove(I);
                source.Overrides[number] = copy;
            }
        }

        /// <summary>
        /// Sets the new value of an existing field. Its rich text value (/RV, ISO 32000-2 12.7.4.3) is the formatted form of the old one and
        /// goes: it contradicted /V (PDF/UA-2 and WTPDF 8.10.3.3 require the same text), readers that show rich text showed the old value,
        /// and an overwritten name stayed in the file (docs/FEHLERGEDAECHTNIS.md, No. 445). /DS (the default style) stays.
        /// </summary>
        internal static void SetValue(PdfDictionary field, PdfObject value)
        {
            field.Set(V, value);
            field.Remove(RV);
        }

        private static readonly PdfName RV = PdfName.Get("RV");

        private static readonly PdfName Pg = PdfName.Get("Pg");

        /// <summary>
        /// Structure element of a redacted or edited page: replacement texts (/ActualText, /Alt, /E) can contain the removed text, so they are
        /// dropped. On a redacted page also what describes the element's content in another form — its XMP and associated files (ISO 32000-2,
        /// 14.13.6: the LaTeX or MathML source of a formula, the data of a figure; docs/FEHLERGEDAECHTNIS.md, No. 444); editing keeps those.
        /// An element over several pages loses them as soon as one of its pages is redacted (on purpose). On a redacted page also its title (/T)
        /// and a table summary in its attributes (/Summary): text about the content as much as a replacement text (docs/FEHLERGEDAECHTNIS.md,
        /// No. 694 — before they stayed and named what the bar took).
        /// </summary>
        /// <param name="source">The file of the element.</param>
        /// <param name="dict">The element as it is written (perhaps refilled with edited text).</param>
        /// <param name="read">The element as read, whose kids tell what it covered; null: <paramref name="dict"/>.</param>
        private static PdfObject WithoutAlternateText(Source source, PdfDictionary dict, PdfDictionary? read = null)
        {
            var alternates = dict.ContainsKey(PdfName.Get("ActualText")) || dict.ContainsKey(PdfName.Get("Alt")) || dict.ContainsKey(PdfName.Get("E"));
            var describing = source.RedactedPages.Count > 0 && dict.ContainsKey(PdfName.Get("S")) && dict.ContainsKey(StructParent) &&
                             (dict.ContainsKey(PdfName.Get("AF")) || dict.ContainsKey(PdfName.Metadata));
            var titled = source.RedactedPages.Count > 0 && dict.ContainsKey(PdfName.Get("S")) &&
                         (dict.ContainsKey(ElementTitleKey) || WithoutSummary(source, dict.GetRaw(AttributesKey)) is not null);
            if (!alternates && !describing && !titled) return dict;
            var redacted = source.RedactedPages.Count > 0 && OnRedactedPage(source, dict);
            if (!redacted && !(alternates && (source.EditedMcids.Count > 0 || source.EditedForms.Count > 0) &&
                               (CoversEditedText(source, dict) || read is not null && !ReferenceEquals(read, dict) && CoversEditedText(source, read))))
                return dict;
            var copy = new PdfDictionary(dict.Count);
            foreach (var (key, value) in dict.Entries)
                if (key.Value is not ("ActualText" or "Alt" or "E") && !(redacted && describing && key.Value is "AF" or "Metadata") && !(redacted && key == ElementTitleKey))
                    copy.Set(key, value);
            if (redacted && WithoutSummary(source, dict.GetRaw(AttributesKey)) is { } attributes) copy.Set(AttributesKey, attributes);
            // edited, not redacted: a neutral description as well, telling it was edited (No. 676; before, a figure or formula was left without)
            NeutralDescription(source, dict, copy, edited: !redacted);
            return copy;
        }

        private static readonly PdfName ElementTitleKey = PdfName.Get("T"), SummaryKey = PdfName.Get("Summary");

        /// <summary>
        /// The attributes of a structure element (/A: an attribute object, an array of them with revision numbers, or references) without
        /// /Summary — a table summary is text about the content (No. 694); null if none has it. Objects of their own are copied in.
        /// </summary>
        private static PdfObject? WithoutSummary(Source source, PdfObject? attributes)
        {
            PdfDictionary? Plain(PdfObject? item)
            {
                var dict = item is PdfReference r ? source.Doc.Resolve(r.Id) as PdfDictionary : item as PdfDictionary;
                if (dict is null || !dict.ContainsKey(SummaryKey)) return null;
                var copy = Clone(dict);
                copy.Remove(SummaryKey);
                return copy;
            }
            switch (attributes)
            {
                case PdfArray array:
                {
                    var items = new PdfObject[Math.Min(array.Count, 10_000)];
                    var changed = false;
                    for (var i = 0; i < items.Length; i++)
                    {
                        var plain = Plain(array[i]);
                        changed |= plain is not null;
                        items[i] = plain ?? array[i];
                    }
                    return changed ? new PdfArray(items) : null;
                }
                default:
                    return Plain(attributes);
            }
        }

        /// <summary>
        /// A structure element of a redacted page that lost its description gets a neutral one where screen readers need one (decision H;
        /// docs/ABLAUFPLAN.md, 6.3 step 3): „Geschwärzter Inhalt“ in a German document, else „Redacted content“ (with /Lang where the text is
        /// not in the language that applies). After the standard type (role map): Figure and Formula always (ISO 14289-1, 7.3 and 7.7); Annot
        /// only if it had /Alt and its annotation has no /Contents (/Alt and /Contents must not differ, ISO 14289-2, 8.9.4.2); Form only if it had
        /// /Alt and its widget has no /TU; Link never (an /Alt would replace the visible text of the link). Nothing of the old text stays: any
        /// description can tell what a bar covers elsewhere on the page (No. 134).
        /// </summary>
        private static void NeutralDescription(Source source, PdfDictionary element, PdfDictionary copy, bool edited = false)
        {
            if (!element.ContainsKey(PdfName.Get("S")) || !element.ContainsKey(StructParent)) return;
            var root = source.Doc.Catalog?.GetDictionary(PdfName.Get("StructTreeRoot"));
            var had = element.ContainsKey(PdfName.Get("Alt"));
            var needed = Accessibility.StructureRoles.Resolve(element, root) switch
            {
                "Figure" or "Formula" => true,
                "Annot" => had && !ObjectsOf(element).Any(o => o.Get(PdfName.Get("Contents")) is PdfString { Bytes.Length: > 0 }),
                "Form" => had && !ObjectsOf(element).Any(o => FieldValue(o, PdfName.Get("TU")) is PdfString { Bytes.Length: > 0 }),
                _ => false,
            };
            if (!needed) return;
            var own = element.GetString(PdfName.Get("Lang"))?.ToText();
            var language = string.IsNullOrEmpty(own) ? source.Doc.Catalog?.GetString(PdfName.Get("Lang"))?.ToText() : own;
            var (text, lang) = edited ? Accessibility.FixedTexts.EditedContent(language) : Accessibility.FixedTexts.RedactedContent(language);
            copy.Set(PdfName.Get("Alt"), new PdfString(PdfTextEncoding.Encode(text)));
            if (lang is not null) copy.Set(PdfName.Get("Lang"), new PdfString(PdfTextEncoding.Encode(lang)));
        }

        /// <summary>The objects a structure element refers to (object references in /K), a few at most.</summary>
        private static IEnumerable<PdfDictionary> ObjectsOf(PdfDictionary element)
        {
            static PdfDictionary? Target(PdfObject? kid) =>
                kid is PdfDictionary objr && objr.GetName(PdfName.Type)?.Value == "OBJR" ? objr.GetDictionary(PdfName.Get("Obj")) : null;
            switch (element.Get(StructKids))
            {
                case PdfArray kids:
                    for (var i = 0; i < kids.Count && i < 64; i++)
                        if (Target(kids.Get(i)) is { } target) yield return target;
                    break;
                case var single when Target(single) is { } target:
                    yield return target;
                    break;
            }
        }

        /// <summary>
        /// An entry of a field, from the widget or the field above it (inherited through /Parent, a few levels). A level without the entry reads
        /// it as the null object: before, the search ended there, and /FT, /Ff and /TU on the parent field — the usual place — were never found
        /// (docs/FEHLERGEDAECHTNIS.md, No. 1004).
        /// </summary>
        private static PdfObject? FieldValue(PdfDictionary widget, PdfName key)
        {
            var d = widget;
            for (var level = 0; d is not null && level < 32; level++, d = d.GetDictionary(PdfName.Get("Parent")))
                if (d.Get(key) is { } value and not PdfNull) return value;
            return null;
        }

        /// <summary>
        /// Whether a formula of the structure (standard type Formula) with an associated file or metadata stream lies on a redacted page: it loses
        /// them (<see cref="WithoutAlternateText"/>). At most 200 000 elements looked at.
        /// </summary>
        private bool FormulaWithSourceRedacted()
        {
            var source = _sources[_main];
            if (source.RedactedPages.Count == 0 || _main.Catalog?.GetDictionary(PdfName.Get("StructTreeRoot")) is not { } root) return false;
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<PdfObject?>();
            stack.Push(root.Get(StructKids));
            var budget = 200_000;
            while (stack.TryPop(out var item) && --budget > 0)
                switch (item)
                {
                    case PdfArray kids:
                        for (var i = 0; i < kids.Count && i < 100_000; i++) stack.Push(kids.Get(i));
                        break;
                    case PdfDictionary element when element.ContainsKey(PdfName.Get("S")) && seen.Add(element):
                        if ((element.ContainsKey(PdfName.Get("AF")) || element.ContainsKey(PdfName.Metadata)) &&
                            Accessibility.StructureRoles.Resolve(element, root) == "Formula" && OnRedactedPage(source, element))
                            return true;
                        stack.Push(element.Get(StructKids));
                        break;
                }
            return false;
        }

        /// <summary>
        /// Whether a redacted page of the file shows characters of the private use areas without a replacement text
        /// (<see cref="Accessibility.PrivateUseText"/>; docs/FEHLERGEDAECHTNIS.md, No. 624). The structure elements of such a page have lost
        /// theirs (<see cref="WithoutAlternateText"/>), so only marked content still giving one counts.
        /// </summary>
        private bool RedactedPrivateUse()
        {
            var source = _sources[_main];
            foreach (var item in _pages)
            {
                if (!ReferenceEquals(item.Source, _main) || item.Page.Id is not { } id || !source.RedactedPages.Contains(id.Number)) continue;
                using var content = Content.PageInterpreter.PageContent(_main, item.Page.Dictionary);
                if (Accessibility.PrivateUseText.ShownWithoutReplacement(_main, content.Span.ToArray(), item.Page.Resources)) return true;
            }
            return false;
        }

        private static readonly PdfName StructKids = PdfName.Get("K");
        private static readonly PdfName StructParent = PdfName.Get("P");

        /// <summary>
        /// Does a structure element belong (even partly) to a redacted page? The page is not always in the element itself (/Pg): it can be
        /// in its content references (MCR, OBJR in /K, also when the element names another page), in an ancestor (without a page of its
        /// own, that of the nearest one applies), or only in an element below it — a parent's /ActualText or /Alt describes its children
        /// as well. Looking only at the element and its direct references left the replacement text of a paragraph whose words lie in
        /// spans under a bar in the file (docs/FEHLERGEDAECHTNIS.md, No. 618; for editing No. 142). Where the tree is too deep or too large
        /// to look at, the element counts as redacted (removing a description is safe, keeping one is not). Results are remembered per element.
        /// </summary>
        private static bool OnRedactedPage(Source source, PdfDictionary element, int depth = 0)
        {
            if (source.RedactedCover.TryGetValue(element, out var known)) return known;
            source.RedactedCover[element] = false;   // cycles in broken trees end here
            bool Redacted(int? page) => page is { } p && source.RedactedPages.Contains(p);
            int? page = element.GetRaw(Pg) is PdfReference own ? own.Id.Number : null;
            var parent = element.GetDictionary(StructParent);
            for (var level = 0; page is null && parent is not null && level < 64; level++, parent = parent.GetDictionary(StructParent))
                if (parent.GetRaw(Pg) is PdfReference inherited) page = inherited.Id.Number;
            bool Kid(PdfObject? kid) => kid switch
            {
                PdfDictionary child when child.ContainsKey(PdfName.Get("S")) =>
                    depth >= 64 || source.RedactedCover.Count >= 200_000 || OnRedactedPage(source, child, depth + 1),
                // a marked-content or object reference: on its own page, else on that of the element
                PdfDictionary reference => Redacted(reference.GetRaw(Pg) is PdfReference p ? p.Id.Number : page),
                _ => false,   // a marked-content identifier lies on the element's page, looked at before
            };
            var covers = Redacted(page);
            switch (element.Get(StructKids))
            {
                case PdfArray kids:
                    covers |= kids.Count > 10_000;
                    for (var i = 0; i < kids.Count && i < 10_000 && !covers; i++) covers = Kid(kids.Get(i));
                    break;
                case var single when !covers:
                    covers = Kid(single);
                    break;
            }
            source.RedactedCover[element] = covers;
            return covers;
        }

        /// <summary>
        /// Does a structure element cover edited text, that is, does its /K (directly, as a content reference, or through an element below
        /// it) point to marked content from which characters were removed? A parent's /ActualText or /Alt describes its children as well —
        /// only looking at the element's own /K left the removed text in a paragraph's replacement text (docs/FEHLERGEDAECHTNIS.md,
        /// No. 142). The page comes from /Pg of the reference, the element or an ancestor. Results are remembered per element.
        /// </summary>
        private static bool CoversEditedText(Source source, PdfDictionary element, int depth = 0)
        {
            if (source.EditedCover.TryGetValue(element, out var known)) return known;
            source.EditedCover[element] = false;   // cycles in broken trees end here
            int? Page(PdfDictionary? d)
            {
                for (var level = 0; d is not null && level < 64; level++, d = d.GetDictionary(StructParent))
                    if (d.GetRaw(Pg) is PdfReference p) return p.Id.Number;
                return null;
            }
            var own = Page(element);
            bool Hit(int? page, long? mcid) => page is { } p && mcid is { } m && source.EditedMcids.TryGetValue(p, out var set) && set.Contains((int)m);
            bool Kid(PdfObject? kid) => kid switch
            {
                PdfInteger number => Hit(own, number.Value),
                // marked content in a form (/Stm): the form was rewritten — characters were removed from it
                PdfDictionary mcr when !mcr.ContainsKey(PdfName.Get("S")) && mcr.GetRaw(PdfName.Get("Stm")) is PdfReference stm =>
                    source.EditedForms.Contains(stm.Id.Number),
                PdfDictionary mcr when !mcr.ContainsKey(PdfName.Get("S")) =>
                    Hit(mcr.GetRaw(Pg) is PdfReference p ? p.Id.Number : own, mcr.GetInteger(PdfName.Get("MCID"))),
                PdfDictionary child when depth < 64 && source.EditedCover.Count < 200_000 => CoversEditedText(source, child, depth + 1),
                _ => false,
            };
            var covers = false;
            switch (element.Get(StructKids))
            {
                case PdfArray kids:
                    for (var i = 0; i < kids.Count && i < 10_000 && !covers; i++) covers = Kid(kids.Get(i));
                    break;
                case var single:
                    covers = Kid(single);
                    break;
            }
            source.EditedCover[element] = covers;
            return covers;
        }

        // ------------------------------------------------------------ Elements

        /// <summary>"q" before the old content: one object for all pages (<see cref="Fresh"/> remembers it).</summary>
        private readonly PdfStream _saveState = new(new PdfDictionary(), "q\n"u8.ToArray());

        /// <summary>Flattening: widgets whose appearance is set anew (see <see cref="FieldAppearance"/>).</summary>
        private readonly Dictionary<PdfDictionary, (ElementAppearance Appearance, bool OverAppearance)> _fieldReplacements = new(ReferenceEqualityComparer.Instance);

        private static bool IsField(PageElement element) => element is TextElement { IsField: true } or SignatureFieldElement or FieldElement;

        /// <summary>Own elements of the page (foreign annotations are also among the elements but are not written anew).</summary>
        private static List<PageElement> OwnElements(PageItem item)
        {
            var own = new List<PageElement>(item.Elements.Count);
            foreach (var element in item.Elements)
                if (element is not ForeignAnnotation) own.Add(element);
            return own;
        }

        /// <summary>Position of each element of the page: foreign ones via their annotation, own ones via themselves (tab order).</summary>
        private static Dictionary<object, int> ElementOrder(PageItem item)
        {
            var order = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < item.Elements.Count; i++)
                order.TryAdd(item.Elements[i] is ForeignAnnotation foreign ? foreign.Original : item.Elements[i], i);
            return order;
        }

        /// <summary>
        /// Are the elements in a different order than the annotations of the file (tab order changed, or an own element before a
        /// foreign one)? Then /Annots is reordered.
        /// </summary>
        private bool Reordered(PageItem item)
        {
            var originals = ForeignAnnotations.For(item.Source, item.Page);
            if (originals.Count == 0) return false;
            var position = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < originals.Count; i++) position.TryAdd(originals[i].Original, i);
            var last = -1;
            var ownSeen = false;
            foreach (var element in item.Elements)
            {
                if (element is ForeignAnnotation foreign && position.TryGetValue(foreign.Original, out var p))
                {
                    if (p < last || ownSeen) return true;
                    last = p;
                }
                else if (element is not ForeignAnnotation && Annotated(element)) ownSeen = true;
            }
            return false;
        }

        /// <summary>A foreign annotation was moved or deleted on the page.</summary>
        private bool ForeignChanged(PageItem item) => _foreignDeleted.ContainsKey(item) || _foreignMoved.ContainsKey(item);

        /// <summary>Is written as an editable annotation (field or stamp), not fixed into the content.</summary>
        private bool Annotated(PageElement element) =>
            // Never flatten redaction marks: the red mark would be fixed in the page while the content underneath is still there
            (!_options.Flatten && ElementStore.StaysEditable(element)) || element is RedactionElement;

        /// <summary>Editable elements were written: marker in the catalogue (otherwise nobody looks for them).</summary>
        private bool _ownElementsWritten;

        /// <summary>
        /// Before the pages are written: per font a subset with the glyphs of all elements (the shared display font is replaced
        /// by it when writing); records the existing field names.
        /// </summary>
        private void PrepareElements()
        {
            var glyphs = new Dictionary<ElementFace, Dictionary<int, string>>(ReferenceEqualityComparer.Instance);
            void Use(ElementAppearance appearance)
            {
                // Per font of the element (formatted text has several; see KONZEPT.md, section 5.14)
                foreach (var (face, faceGlyphs) in appearance.FaceUses)
                {
                    if (!glyphs.TryGetValue(face, out var used)) glyphs[face] = used = [];
                    foreach (var (glyph, text) in faceGlyphs) used.TryAdd(glyph, text);
                }
            }
            foreach (var item in _pages)
            {
                foreach (var element in OwnElements(item)) Use(ElementAppearance.Final(element));
                // Flattening: fields and free text without a (valid) appearance get one, otherwise their text would be lost; the
                // same appearance the display shows (FieldAppearance.TextAppearance). PDF/A: free text without an appearance gets that one as
                // its appearance (6.3.3; the fields get theirs when their form is settled, see SettleAppearances)
                if ((_options.Flatten || AppearancesRequired) && item.Page.Dictionary.Get(Annots) is PdfArray annots)
                    for (var i = 0; i < annots.Count; i++)
                        if (annots.Get(i) is PdfDictionary annot && !_fieldReplacements.ContainsKey(annot) && !OwnedAnnotations().Contains(annot) &&
                            !_filledWidgets.Contains(annot) &&   // filled in: already has its new appearance
                            annot.GetName(PdfName.Subtype)?.Value is "Widget" or "FreeText" && (_options.Flatten || annot.GetName(PdfName.Subtype)?.Value == "FreeText") &&
                            FieldAppearance.TextAppearance(item.Source, annot) is { } replacement)
                        {
                            _fieldReplacements[annot] = replacement;
                            Use(replacement.Appearance);
                        }
            }
            foreach (var (_, appearance) in _filledForms) Use(appearance);
            foreach (var (face, used) in glyphs)
            {
                var number = NewNumber();
                _memoryNumbers[face.DisplayFont] = number;
                _memoryQueue.Enqueue((number, WithOwnCidSet(face.SavedFont(used))));
            }
            // Filled-in text and choice fields: the new appearance (the font now as a subset)
            foreach (var (widget, appearance) in _filledForms)
            {
                var ap = new PdfDictionary();
                var form = WrittenForm(appearance);
                // PDF/A: the normal appearance of a button is a set of states (ISO 19005-2, 6.3.3) — a push button settled with its caption
                // (No. 889) gets it as the state it shows, as the PDF/A step does for a button's one appearance; that step passes over what
                // the writer made itself. Before, the PDF/A copy of resetform_parent broke 6.3.3 (docs/FEHLERGEDAECHTNIS.md, No. 919).
                if (_pdfA is not null && (FieldType(widget) ?? _relinkedFieldTypes.GetValueOrDefault(widget)) == "Btn")
                {
                    var shown = widget.GetName(AS)?.Value ?? (widget.Get(V) as PdfName)?.Value ?? "On";
                    var states = new PdfDictionary();
                    states.Set(PdfName.Get(shown), form);
                    ap.Set(PdfName.N, states);
                    widget.Set(AS, PdfName.Get(shown));
                }
                else ap.Set(PdfName.N, form);
                widget.Set(PdfName.Get("AP"), ap);
            }
            // Existing field names (also without text elements: a new signature field needs a free name).
            // Not own fields: they are written anew with their name (otherwise they would be called "Name1" afterwards)
            var owned = OwnedAnnotations();
            // Renamed foreign fields with their new name (otherwise a new field with the old name would be called "A1")
            var renamed = new Dictionary<PdfDictionary, string>(ReferenceEqualityComparer.Instance);
            foreach (var item in _pages)
                foreach (var element in item.Elements)
                    if (element is ForeignAnnotation { Spec: { } spec } foreign && FormField.Of(foreign) is { } formField)
                        renamed[formField.Field] = spec.Name;
            foreach (var source in _sourceOrder)
                if (source.Doc.Catalog?.GetDictionary(AcroForm)?.GetArray(Fields) is { } list)
                    for (var i = 0; i < list.Count; i++)
                        if (list.Get(i) is PdfDictionary field && !owned.Contains(field) &&
                            (renamed.TryGetValue(field, out var now) ? now : field.GetString(T)?.ToText()) is { } name) _fieldNames.Add(name);
            // the names of linked groups are taken (their own members do not ask for a name)
            foreach (var group in _newLinkFields) _fieldNames.Add(group.Name);
        }

        /// <summary>
        /// Elements of a page: fields as widgets, texts and images as stamp annotations (both with <see cref="ElementStore.Key"/>,
        /// editable again when opened next time), signatures fixed into the content. When flattening, everything goes into the
        /// content, including existing annotations with an appearance.
        /// </summary>
        private void AddElements(PdfDictionary dict, PageItem item, Source source, int self)
        {
            var page = item.Page;
            var ownElements = OwnElements(item);
            var annotated = ownElements.Where(Annotated).Select(ElementAppearance.Final).ToList();
            var flat = ownElements.Where(e => !Annotated(e) && Printed(e)).Select(ElementAppearance.Final).ToList();
            var owned = DroppedAnnotations(item);
            // the old elements of own annotations leave the tree before the new ones go in (No. 633)
            ForgetRewrittenAnnotations(item);
            var drawn = new List<(PdfObject Form, Graphics.Matrix ToPage)>();
            // flattened annotations of a tagged copy: the marked content around what each draws, by the index of its first form (No. 632)
            var marks = new Dictionary<int, (ContentMark Mark, int Count)>();
            var overLimit = AnnotationsOverLimit(page.Dictionary);
            _addedAnnotations.TryGetValue(page.Dictionary, out var addedAnnots);
            if (annotated.Count > 0 || _options.Flatten || owned.Count > 0 || ForeignChanged(item) || Reordered(item) || addedAnnots is { Count: > 0 } ||
                BannedAnnotations(page.Dictionary))
            {
                var annots = new PdfArray();
                var order = new List<double>();   // tab order: position of the element in the page per entry
                var annotElements = new List<PdfObject?>();   // in a tagged copy: the element of each entry (No. 636)
                var structure = !_options.Flatten && ReferenceEquals(item.Source, _main) ? StructureEdits() : null;
                PdfObject? ElementOfEntry(PdfDictionary? annot) =>
                    structure is not null && NumberTree.KeyOf(annot?.Get(StructParentKey)) is { } structKey ? structure.ElementForKey(structKey) : null;
                var labels = ownElements.OfType<TextElement>().Where(t => t.LabelFor.Length > 0).ToList();
                var elementIndex = ElementOrder(item);
                double last = -1;
                var movedHere = _foreignMoved.GetValueOrDefault(item);
                if (page.Dictionary.Get(Annots) is PdfArray existing)
                    for (var i = 0; i < existing.Count; i++)
                    {
                        // Own ones come anew as elements; deleted foreign ones are dropped
                        if (existing.Get(i) is PdfDictionary own && owned.Contains(own)) continue;
                        // Moved: as a reference, Overrides replaces the object; entered directly, the copy itself; shared (also
                        // on another page), a copy as a separate object for this page only
                        PdfDictionary? moved = null;
                        var copy = false;
                        if (movedHere is not null && existing.Get(i) is PdfDictionary m && movedHere.TryGetValue(m, out var entry)) (moved, copy) = entry;
                        // PDF/A: a type the part does not allow leaves the page (6.3.1), what the display draws of it goes into the content
                        // (PDF/A-1: so do the comments beyond the most entries a list may have, AnnotationsOverLimit)
                        if (_pdfA is not null && (moved ?? existing.Get(i)) is PdfDictionary banned &&
                            (!PdfACheck.AllowedAnnotation(banned, _pdfA.Part) || existing.Get(i) is PdfDictionary listed && overLimit?.Contains(listed) == true))
                        {
                            var shownFrom = drawn.Count;
                            if (FlatAnnotation(banned, source, page.Rotate) is { } shown)
                            {
                                if (NumberOf(shown.Form, source) is > 0 and var appearance) _pdfA.Appearances.Add(appearance);
                                drawn.Add((Rebase(shown.Form, source), shown.ToPage));
                            }
                            // without an appearance: what the display draws from its properties (an appearance in page space)
                            else if (!HiddenAnnotation(banned, source) && banned.GetDictionary(PdfName.Get("AP")) is null &&
                                     Content.AnnotationAppearance.RectOf(banned) is { } area && Content.AnnotationAppearance.Build(banned, area) is { } built)
                            {
                                var look = Fresh(built);
                                _pdfA.Appearances.Add(((OutputReference)look).Number);
                                drawn.Add((look, Graphics.Matrix.Identity));
                            }
                            // in a tagged copy as when flattening: what it drew is marked content of its element, or an artifact (No. 678;
                            // before it was untagged content, and a PDF/UA identification stayed over it)
                            if (FlattenedIntoContent(banned, existing[i], source, self, drawn.Count > shownFrom) is { } bannedMark)
                                marks[shownFrom] = (bannedMark, drawn.Count - shownFrom);
                            continue;
                        }
                        if (!_options.Flatten)
                        {
                            // Without a position of its own (pop-up, link): directly after the previous entry
                            order.Add(last = existing.Get(i) is PdfDictionary keyed && elementIndex.TryGetValue(keyed, out var at) ? at : last);
                            if (copy)
                            {
                                var number = NewNumber();
                                _memoryQueue.Enqueue((number, Rebase(moved!, source)));
                                annots.Add(new OutputReference(number));
                            }
                            else annots.Add(moved is not null && existing[i] is not PdfReference ? Rebase(moved, source) : Rebase(existing[i], source));
                            annotElements.Add(ElementOfEntry((moved ?? existing.Get(i)) as PdfDictionary));
                            continue;
                        }
                        // Flattening: appearance into the content; only links stay annotations
                        if ((moved ?? existing.Get(i)) is not PdfDictionary annot) continue;
                        if (annot.GetName(PdfName.Subtype)?.Value == "Link")
                        {
                            annots.Add(Rebase(existing[i], source));
                            continue;
                        }
                        var first = drawn.Count;
                        if (_options.ForPrint && HiddenAnnotation(annot, source))
                        {
                            // left out
                        }
                        else if (existing.Get(i) is PdfDictionary filledKey && _filledText.TryGetValue(filledKey, out var filled))
                            drawn.Add((WrittenForm(filled), filled.FormToPage()));   // filled in: the new appearance
                        else if (existing.Get(i) is PdfDictionary key && _fieldReplacements.TryGetValue(key, out var replaced))
                        {
                            if (!HiddenAnnotation(annot, source))
                            {
                                // Border and background from the existing appearance, the value on top
                                if (replaced.OverAppearance && FlatAnnotation(annot, source, page.Rotate) is { } frame) drawn.Add((Rebase(frame.Form, source), frame.ToPage));
                                drawn.Add((WrittenForm(replaced.Appearance), replaced.Appearance.FormToPage()));
                            }
                        }
                        else if (FlatAnnotation(annot, source, page.Rotate) is { } flatAnnot) drawn.Add((Rebase(flatAnnot.Form, source), flatAnnot.ToPage));
                        // Without /AP: the appearance the display generates (rectangle, line, markup …), already in page space
                        else if (!HiddenAnnotation(annot, source) && annot.GetDictionary(PdfName.Get("AP"))?.Get(PdfName.N) is null or PdfNull &&
                                 Content.AnnotationAppearance.RectOf(annot) is { } annotRect &&
                                 Content.AnnotationAppearance.Build(annot, annotRect) is { } generated)
                            drawn.Add((Fresh(generated), Graphics.Matrix.Identity));
                        // in a tagged copy: what it drew is marked content of its element, or an artifact; what draws nothing leaves the tree
                        if (FlattenedIntoContent(annot, existing[i], source, self, drawn.Count > first) is { } mark) marks[first] = (mark, drawn.Count - first);
                    }
                foreach (var appearance in annotated)
                {
                    var number = NewNumber();
                    annots.Add(new OutputReference(number));
                    order.Add(elementIndex.TryGetValue(appearance.Element, out var at) ? at : int.MaxValue);
                    // New form field: an ordinary field without an own marker; when opened next time, a field like those of other programs
                    if (appearance.Element is FieldElement field)
                    {
                        var newField = NewField(field, appearance, self, number, labels);
                        annotElements.Add(TagNewAnnotation(newField, number, item, self, "Form"));
                        _memoryQueue.Enqueue((number, newField));
                        continue;
                    }
                    var isField = IsField(appearance.Element);
                    var written = isField ? Widget(appearance, self) : Stamp(appearance, self);
                    // in a tagged copy: an element of its own beside the content it sits on (No. 633)
                    annotElements.Add(TagNewAnnotation(written, number, item, self, isField ? "Form" : "Annot"));
                    _memoryQueue.Enqueue((number, written));
                    if (isField) _newFields.Add(number);
                    _ownElementsWritten = true;
                }
                // added annotations (the notes at redaction bars): at the end, with the page as their /P
                foreach (var added in addedAnnots ?? [])
                {
                    var number = NewNumber();
                    var annot = (PdfDictionary)Fresh(added);
                    annot.Set(PdfName.Get("P"), new OutputReference(self));
                    // made before the version was known (the note of a redaction reason): its date in the form of the version written
                    if (annot.GetString(PdfName.Get("M"))?.ToText() is { } modified && Document.PdfDate.Parse(modified) is { } when)
                        annot.Set(PdfName.Get("M"), new PdfString(System.Text.Encoding.ASCII.GetBytes(PdfDate(when))));
                    annots.Add(new OutputReference(number));
                    order.Add(int.MaxValue);
                    annotElements.Add(TagNewAnnotation(annot, number, item, self, "Annot"));
                    _memoryQueue.Enqueue((number, annot));
                }
                // Tab order changed: entries in the order of the elements; /Tabs (rows, columns, structure) would otherwise take precedence.
                // In a tagged file the order follows the structure (/Tabs /S, ISO 14289-1, 7.18.3; ISO 14289-2, 8.9.3.3): /Tabs stays, the
                // entries are still written in the chosen order, and the user is told (docs/FEHLERGEDAECHTNIS.md, No. 491)
                var tagged = TaggedCopy();
                if (!_options.Flatten && order.Count == annots.Count && order.Zip(order.Skip(1)).Any(p => p.Second < p.First))
                {
                    var sortedIndices = Enumerable.Range(0, annots.Count).OrderBy(i => order[i]).ToList();
                    annots = [.. sortedIndices.Select(i => annots[i])];
                    // in a tagged copy the order goes into the tree where it can (No. 636); else the save says it follows the structure (No. 491)
                    if (tagged && !(annotElements.Count == order.Count && TabOrderIntoTree([.. sortedIndices.Select(i => annotElements[i])]))) _tabOrderByStructure = true;
                    else if (!tagged) dict.Remove(TabsKey);
                }
                if (annots.Count > 0)
                {
                    dict.Set(Annots, annots);
                    // a page of a tagged file with annotations: in the order of the structure (No. 491)
                    if (tagged && dict.Get(TabsKey) is not PdfName { Value: "S" }) dict.Set(TabsKey, PdfName.Get("S"));
                }
                else dict.Remove(Annots);
            }
            foreach (var appearance in flat)
            {
                // in a tagged copy: the element's own structure (No. 634)
                if (FixedIntoContent(appearance, item, self) is { } fixedMark) marks[drawn.Count] = (fixedMark, 1);
                drawn.Add((FlatForm(appearance, dict), appearance.FormToPage()));
            }
            if (_overlays.TryGetValue(page.Dictionary, out var overlays))
                foreach (var overlay in overlays)
                {
                    // a text layer of the text recognition in a tagged copy: a paragraph (6.6 i, No. 657)
                    if (TextLayerIntoContent(overlay, item, self) is { } layerMark) marks[drawn.Count] = (layerMark, 1);
                    drawn.Add((Fresh(overlay), Graphics.Matrix.Identity));
                }
            if (drawn.Count == 0)
            {
                // Content and resources were skipped: take them over unchanged,
                // as they are in the page: as a reference, a shared object stays shared; inherited
                // resources are set afterwards by the common path for inherited values (once for all pages)
                if (page.Dictionary.GetRaw(PdfName.Contents) is { } c) dict.Set(PdfName.Contents, Rebase(c, source));
                if (page.Dictionary.GetRaw(PdfName.Resources) is { } res) dict.Set(PdfName.Resources, Rebase(res, source));
                return;
            }

            // Content: q, old content, Q, then annotations and elements; the old content can leave the state changed. If it leaves
            // states open or closes too many, that many more q or Q (otherwise every reader would draw the elements about ten
            // times too small when the page starts with "q 0.1 0 0 0.1 0 0 cm" and the matching Q is missing)
            // A Q that closes more than was opened goes from the content first: before the q it would restore the initial state where
            // readers pass over it (docs/FEHLERGEDAECHTNIS.md, No. 383)
            PdfObject? nested = null;
            List<(PdfName Name, PdfStream Image)>? inlineImages = null;
            var old = new List<PdfObject>();
            var (open, close) = (1, 1);
            if (page.Dictionary.Get(PdfName.Contents) is PdfArray or PdfStream && _pdfA is not null &&
                PdfAContent(page.Dictionary, page.Resources, source, out nested) is { } content)
                old.Add(content);   // balanced: every q has its Q (ContentNesting)
            else if (_pdfA is null && PageInlineImageContent(page.Dictionary, source) is var (inlineContent, images))
            {
                // an inline image the display reads only with a tolerance: an XObject of the page (see PageInlineImages; No. 993)
                var single = new PdfStream(new PdfDictionary(), inlineContent);
                var (cleanedParts, balance) = Balanced(source.Doc, [single]);
                (open, close) = balance;
                old.Add(Fresh(cleanedParts?[0] is { } cleanedPart ? new PdfStream(new PdfDictionary(), cleanedPart) : single));
                inlineImages = images;
            }
            else
            {
                var raw = page.Dictionary.Get(PdfName.Contents) switch
                {
                    PdfArray parts => Enumerable.Range(0, parts.Count).Select(i => parts[i]).ToList(),
                    PdfStream when page.Dictionary.GetRaw(PdfName.Contents) is { } single => [single],
                    _ => new List<PdfObject>(),
                };
                var (streams, balanced) = Balanced(source.Doc, raw);
                (open, close) = balanced;
                for (var i = 0; i < raw.Count; i++)
                    old.Add(streams?[i] is { } cleaned ? Fresh(new PdfStream(new PdfDictionary(), cleaned)) : Rebase(raw[i], source));
            }
            var contents = new PdfArray([open == 1 ? Fresh(_saveState) : Fresh(new PdfStream(new PdfDictionary(), System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("q\n", open)))))]);
            foreach (var part in old) contents.Add(part);
            // Resources of its own for this page (forms are added); /XObject as a copy, not additionally taken over via its
            // reference, otherwise the old object would remain unused in the file
            var resources = new PdfDictionary();
            if (page.Resources is { } r)
                foreach (var (key, value) in r.Entries)
                    if (key != XObject) resources.Set(key, Rebase(value, source));
            var xobjects = page.Resources?.Get(XObject) is PdfDictionary x ? (PdfDictionary)Rebase(x, source) : new PdfDictionary();
            // PDF/A: the forms content nested too deep moved into (PdfAContent)
            if (nested is PdfDictionary { } withNests && withNests.Get(XObject) is PdfDictionary nestForms)
                foreach (var (key, value) in nestForms.Entries) xobjects.Set(key, value);
            if (inlineImages is not null)
                foreach (var (name, image) in inlineImages) xobjects.Set(name, Fresh(image));
            var text = new System.Text.StringBuilder(string.Concat(Enumerable.Repeat("Q\n", close)));
            var k = 0;
            var closeAt = -1;
            for (var d = 0; d < drawn.Count; d++)
            {
                var (form, m) = drawn[d];
                // the marked content around the forms of a flattened annotation (ISO 32000-2, 14.6, 14.7.5.2; never inside its appearance)
                if (marks.TryGetValue(d, out var marked))
                {
                    text.Append(System.Text.Encoding.ASCII.GetString(PdfSerializer.ToBytes(marked.Mark.Tag)))
                        .Append(marked.Mark.Mcid is { } mcid ? $" <</MCID {mcid}>> BDC\n" : " <</Type /Layout>> BDC\n");
                    closeAt = d + marked.Count - 1;
                }
                PdfName name;
                do name = PdfName.Get($"PKn{++k}");
                while (xobjects.ContainsKey(name));
                xobjects.Set(name, form);
                text.Append("q ");
                foreach (var v in (ReadOnlySpan<double>)[m.A, m.B, m.C, m.D, m.E, m.F]) text.Append(PdfSerializer.FormatReal(Math.Round(v, 6))).Append(' ');
                text.Append("cm /").Append(name.Value).Append(" Do Q\n");
                if (d == closeAt) text.Append("EMC\n");
            }
            contents.Add(Fresh(new PdfStream(new PdfDictionary(), System.Text.Encoding.ASCII.GetBytes(text.ToString()))));
            resources.Set(XObject, xobjects);
            dict.Set(PdfName.Contents, contents);
            dict.Set(PdfName.Resources, resources);
        }

        /// <summary>
        /// The parts of the original content without a Q that closes more than was opened (null where a part has none, or for all if there is
        /// none), and how many q before and Q after it are needed so that the initial state applies again afterwards: one, plus what it leaves
        /// open. Unreadable content: as it is, one each. At most 4096 (crafted files).
        /// </summary>
        private static (byte[]?[]? Streams, (int Open, int Close) Balance) Balanced(PdfDocument source, List<PdfObject> parts)
        {
            try
            {
                // part by part, within the bound of all parts together (No. 1002): beyond it as for a single stream above the limit
                var streams = new List<byte[]>(parts.Count);
                long total = 0;
                foreach (var p in parts)
                {
                    var data = (p is PdfReference r ? source.Resolve(r.Id) : p) is PdfStream s ? source.GetStreamData(s) : [];
                    total += data.Length + 1L;
                    if (total > PdfKnead.Core.Pdf.Content.PageInterpreter.ContentPartsLimit(parts.Count)) throw new PdfLimitException(Tr.T("Der Seiteninhalt ist zu groß."));
                    streams.Add(data);
                }
                var cleaned = Content.StateBalance.WithoutStrayRestores(streams);
                if (cleaned.All(c => c is null)) return (null, Content.StateBalance.Of(streams));
                return (cleaned, Content.StateBalance.Of(streams.Select((s, i) => cleaned[i] ?? s)));
            }
            catch (Exception e) when (e is PdfException or InvalidDataException or IOException)
            {
                return (null, (1, 1));
            }
        }

        /// <summary>
        /// Form of an element that is fixed into the page. Edited text (see KONZEPT.md, section 5.14) carries its text and
        /// formatting, and the page carries the marker, so PDF Knead turns it into a block again when opening
        /// (<see cref="ContentElements"/>); when flattened, without either.
        /// </summary>
        private PdfObject FlatForm(ElementAppearance appearance, PdfDictionary page)
        {
            if (appearance.Element is not RichTextElement rich || appearance.Form.RawData is not { } data) return Fresh(appearance.Form);
            var dict = new PdfDictionary(appearance.Form.Dictionary.Count + 1);
            foreach (var (key, value) in appearance.Form.Dictionary.Entries)
                if (key != PdfName.Resources) dict.Set(key, value);
            // Fonts: own ones as subsets (when writing), those of the file as a reference into their source; otherwise their references would be dropped
            var fonts = new PdfDictionary();
            if (appearance.Form.Dictionary.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("Font")) is { } used)
                foreach (var (name, value) in used.Entries)
                    fonts.Set(name, appearance.OriginalFonts.TryGetValue(name, out var original)
                        ? Rebase(original.Id is { } id ? new PdfReference(id) : original.Font, AddSource(original.Document))
                        : value);
            var resources = new PdfDictionary();
            resources.Set(PdfName.Get("Font"), fonts);
            dict.Set(PdfName.Resources, resources);
            if (!_options.Flatten)
            {
                dict.Set(ElementStore.Key, ContentElements.Describe(rich, appearance.OriginalFonts));
                page.Set(ContentElements.PageKey, PdfBoolean.Of(true));
            }
            var number = NewNumber();
            _memoryQueue.Enqueue((number, new PdfStream(dict, data)));
            return new OutputReference(number);
        }

        /// <summary>When printing (12.5.3): only with the Print flag and not Hidden; "print only" (NoView) is included.</summary>
        private static bool Printed(PdfDictionary annot) => ((annot.GetInteger(PdfName.F) ?? 0) & (2 | 4)) == 4;

        /// <summary>Own element for printing: fields "hidden" and "do not print" are dropped.</summary>
        private bool Printed(PageElement element) =>
            !_options.ForPrint || element is not FieldElement { Spec.Visibility: FieldVisibility.Hidden or FieldVisibility.NoPrint };

        /// <summary>Hidden, not to be displayed (when printing: not to be printed), a pop-up, or on a hidden layer.</summary>
        /// <summary>PDF/A: the page has annotations of a type the part does not allow (6.3.1); what the display draws of them goes into its content.</summary>
        private bool BannedAnnotations(PdfDictionary page) =>
            _pdfA is not null && page.Get(Annots) is PdfArray annots &&
            (Enumerable.Range(0, Math.Min(annots.Count, 100_000)).Any(i => annots.Get(i) is PdfDictionary a && !PdfACheck.AllowedAnnotation(a, _pdfA.Part))
             || AnnotationsOverLimit(page) is not null);

        private readonly Dictionary<PdfDictionary, HashSet<PdfDictionary>?> _annotationsOverLimit = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// PDF/A-1 allows no array of more than 8 191 entries (ISO 19005-1, 6.1.12): on a page with more annotations, fields stay, then links
        /// (they do something) and then comments up to that number; the comments beyond go into the content as the display draws them, as the
        /// types the part bans do, and links beyond — which draw nothing — are left out (the page looks the same; only they no longer lead
        /// anywhere). Null where the page's annotations fit (docs/FEHLERGEDAECHTNIS.md, No. 337).
        /// </summary>
        private HashSet<PdfDictionary>? AnnotationsOverLimit(PdfDictionary page)
        {
            if (_pdfA?.Part.Part() != 1 || page.Get(Annots) is not PdfArray { Count: > PdfAListLimit } annots) return null;
            if (_annotationsOverLimit.TryGetValue(page, out var known)) return known;
            var count = Math.Min(annots.Count, 100_000);
            var allowed = Enumerable.Range(0, count).Select(i => annots.Get(i) as PdfDictionary)
                .OfType<PdfDictionary>().Where(a => PdfACheck.AllowedAnnotation(a, _pdfA.Part)).ToList();
            var room = PdfAListLimit - allowed.Count(a => a.GetName(PdfName.Subtype)?.Value == "Widget");
            var over = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            foreach (var kind in (ReadOnlySpan<bool>)[true, false])   // links first, then the others
                foreach (var a in allowed)
                {
                    var subtype = a.GetName(PdfName.Subtype)?.Value;
                    if (subtype == "Widget" || (subtype == "Link") != kind) continue;
                    if (room > 0) room--;
                    else over.Add(a);
                }
            return _annotationsOverLimit[page] = over.Count > 0 ? over : null;
        }

        /// <summary>The most entries an array may have in PDF/A-1 (ISO 19005-1, 6.1.12).</summary>
        private const int PdfAListLimit = 8191;

        private bool HiddenAnnotation(PdfDictionary annot, Source source) =>
            (_options.ForPrint ? !Printed(annot) : ((annot.GetInteger(PdfName.F) ?? 0) & (2 | 32)) != 0) || annot.GetName(PdfName.Subtype)?.Value == "Popup" ||
            (_options.ForPrint ? source.PrintOptionalContent : source.OptionalContent).IsHidden(annot.Get(PdfName.Get("OC")), null);

        /// <summary>
        /// Annotation for flattening: reference to its appearance (/AP /N; with states, the current one) and the mapping onto its
        /// /Rect (12.5.5, as when drawing). Null: hidden, without appearance, or a pop-up; it is then dropped.
        /// </summary>
        private (PdfObject Form, Graphics.Matrix ToPage)? FlatAnnotation(PdfDictionary annot, Source source, int pageRotate)
        {
            if (HiddenAnnotation(annot, source)) return null;
            if (annot.GetDictionary(PdfName.Get("AP")) is not { } ap) return null;
            PdfObject? raw;
            PdfStream stream;
            if (ap.Get(PdfName.N) is PdfStream single)
            {
                raw = ap.GetRaw(PdfName.N);
                stream = single;
            }
            else if (ap.Get(PdfName.N) is PdfDictionary states && annot.GetName(PdfName.Get("AS")) is { } state && states.Get(state) is PdfStream chosen)
            {
                raw = states.GetRaw(state);
                stream = chosen;
            }
            else
            {
                return null;
            }
            if (raw is null || Document.PdfRectangle.From(annot.Get(PdfName.Get("Rect"))) is not { } rect ||
                Document.PdfRectangle.From(stream.Dictionary.Get(PdfName.Get("BBox"))) is not { } bbox) return null;
            var formMatrix = Graphics.Matrix.FromArray(stream.Dictionary.GetArray(PdfName.Get("Matrix"))) ?? Graphics.Matrix.Identity;
            var t = formMatrix.TransformBounds(Graphics.RectD.FromLtrb(bbox.Left, bbox.Bottom, bbox.Right, bbox.Top));
            if (t.Width <= 0 || t.Height <= 0) return null;
            var a = Graphics.Matrix.Translate(-t.Left, -t.Top) * Graphics.Matrix.Scale(rect.Width / t.Width, rect.Height / t.Height)
                    * Graphics.Matrix.Translate(rect.Left, rect.Bottom);
            // NoRotate on a rotated page: upright as in the display
            if (((annot.GetInteger(PdfName.Get("F")) ?? 0) & 16) != 0 && pageRotate != 0)
                a = a * Graphics.Matrix.Translate(-rect.Left, -rect.Top) * Graphics.Matrix.Rotate(pageRotate) * Graphics.Matrix.Translate(rect.Left, rect.Top);
            // As a form XObject the stream needs /Subtype /Form (many programs omit it for annotations)
            if (raw is PdfReference reference && stream.Dictionary.GetName(PdfName.Subtype)?.Value != "Form")
            {
                var dict = Clone(stream.Dictionary);
                dict.Set(PdfName.Type, XObject);
                dict.Set(PdfName.Subtype, PdfName.Get("Form"));
                source.Overrides[reference.Id.Number] = stream.RawData is { } data ? new PdfStream(dict, data) : new PdfStream(dict, stream.DataOffset, stream.DataLength, stream.Id);
            }
            return (raw, a);
        }

        /// <summary>The language of the document as written: as given for this save, else the catalogue's (for fixed texts in the file).</summary>
        private string? DocumentLanguage => _options.DocumentLanguage is { Length: > 0 } given ? given : _main.Catalog?.GetString(LangKey)?.ToText() ?? InheritedLanguage();

        /// <summary>
        /// Text or image as a stamp annotation (12.5.6.12) with a finished appearance: every program shows and prints it as it
        /// would fixed content; PDF Knead recognises it again by <see cref="ElementStore.Key"/>. Every kind says what it is in /Contents (text,
        /// code, note, dimension: their text; image: its alternative text; shape: its kind; redaction mark: so and its reason; docs/ABLAUFPLAN.md,
        /// 6.4 step 3) — in the document's language, without names of persons.
        /// </summary>
        private PdfDictionary Stamp(ElementAppearance appearance, int page)
        {
            var annot = new PdfDictionary();
            foreach (var (key, value) in appearance.Annotation.Entries) annot.Set(key, value);
            annot.Set(PdfName.Subtype, PdfName.Get("Stamp"));
            annot.Set(PdfName.Get("P"), new OutputReference(page));
            if (appearance.Element is RedactionElement redaction)
            {
                // Redaction mark (12.5.6.23): common PDF editors recognise it and can apply it themselves
                annot.Set(PdfName.Subtype, PdfName.Get("Redact"));
                var quads = new PdfArray();
                foreach (var a in redaction.PageAreas)
                    foreach (var v in (ReadOnlySpan<double>)[a.Left, a.Top, a.Right, a.Top, a.Left, a.Bottom, a.Right, a.Bottom])
                        quads.Add(new PdfReal(Math.Round(v, 3)));
                annot.Set(PdfName.Get("QuadPoints"), quads);
                annot.Set(PdfName.Get("IC"), new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(0)]));
                // the code (or own reason) as the text other editors write into the bar when they apply it, the reason in full as its
                // contents (12.5.6.23; wish of 4.11.2026)
                if (redaction.BarText is { Length: > 0 } overlay)
                {
                    annot.Set(PdfName.Get("OverlayText"), new PdfString(PdfTextEncoding.Encode(overlay)));
                    annot.Set(PdfName.Get("DA"), new PdfString(System.Text.Encoding.ASCII.GetBytes("/Helv 0 Tf 1 1 1 rg")));
                }
                // what it is, and the reason in full (12.5.6.23; wish of 4.11.2026) — screen readers read it (docs/ABLAUFPLAN.md, 6.4 step 3)
                var reason = redaction.Reason is { Length: > 0 } why ? redaction.ReasonCode is { } c ? $"{c} — {why}" : why : null;
                annot.Set(PdfName.Contents, new PdfString(PdfTextEncoding.Encode(Accessibility.FixedTexts.RedactionMark(reason, DocumentLanguage))));
            }
            if (appearance.Element is ShapeElement shape)
            {
                ShapeAnnotation(annot, shape, appearance);
                annot.Set(PdfName.Contents, new PdfString(PdfTextEncoding.Encode(Accessibility.FixedTexts.Shape(shape.Kind, DocumentLanguage))));
            }
            if (appearance.Element is DimensionElement dimension) DimensionAnnotation(annot, dimension, Pdf20);
            if (appearance.Element is NoteElement note)
            {
                // a note (12.5.6.4): other readers list and open it; not printed with the page, keeps its size when zooming
                annot.Set(PdfName.Subtype, PdfName.Get("Text"));
                annot.Set(PdfName.Contents, new PdfString(PdfTextEncoding.Encode(note.Text)));
                if (note.Author.Length > 0) annot.Set(T, new PdfString(PdfTextEncoding.Encode(note.Author)));
                annot.Set(PdfName.Get("M"), new PdfString(System.Text.Encoding.ASCII.GetBytes(PdfDate(note.Modified))));
                annot.Set(PdfName.Get("Name"), PdfName.Get("Comment"));
                annot.Set(PdfName.Get("F"), PdfInteger.Of(8 | 16));
                annot.Set(PdfName.Get("C"), new PdfArray([new PdfReal(Math.Round(((note.Color >> 16) & 0xFF) / 255.0, 4)),
                    new PdfReal(Math.Round(((note.Color >> 8) & 0xFF) / 255.0, 4)), new PdfReal(Math.Round((note.Color & 0xFF) / 255.0, 4))]));
            }
            if (appearance.Element is TextElement text)
                annot.Set(PdfName.Contents, new PdfString(PdfTextEncoding.Encode(text.Text)));   // for comment lists and screen readers
            if (appearance.Element is CodeElement code)
                annot.Set(PdfName.Contents, new PdfString(PdfTextEncoding.Encode(ElementAppearance.CodeAltText(code))));   // read by screen readers
            if (appearance.Element is ImageElement described)
            {
                // without /Contents screen readers announce a stamp by its default name „Draft“ (12.5.6.12; ISO 14289-1, 7.18.1); a stamp that
                // is an image says so from 2.0 on (/IT /StampImage)
                annot.Set(PdfName.Contents, new PdfString(PdfTextEncoding.Encode(Accessibility.FixedTexts.Image(described, DocumentLanguage))));
                if (Pdf20) annot.Set(PdfName.Get("IT"), PdfName.Get("StampImage"));
            }
            var image = appearance.Element is ImageElement picture ? ElementAppearance.SavedImageStream(picture) : null;   // the same as in the appearance (downsampled, sharpened)
            annot.Set(ElementStore.Key, ElementStore.Describe(appearance.Element, image));
            return annot;
        }

        /// <summary>
        /// Shape (see KONZEPT.md, section 5.13) as an ordinary annotation: rectangle → Square, ellipse → Circle, line and arrow →
        /// Line (/L, arrow as /LE ClosedArrow), triangle, diamond, star, speech bubble → Polygon (/Vertices); colours /C and /IC,
        /// border /BS, opacity /CA. Other programs can edit it this way and redraw it themselves if needed.
        /// </summary>
        private static void ShapeAnnotation(PdfDictionary annot, ShapeElement shape, ElementAppearance appearance)
        {
            static PdfArray Color(int c) => new([new PdfReal(Math.Round(((c >> 16) & 0xFF) / 255.0, 4)), new PdfReal(Math.Round(((c >> 8) & 0xFF) / 255.0, 4)),
                new PdfReal(Math.Round((c & 0xFF) / 255.0, 4))]);
            var toPage = appearance.UprightToPage();
            PdfArray Points(IEnumerable<(double X, double Y)> points)
            {
                var array = new PdfArray();
                foreach (var (x, y) in points)
                {
                    var p = toPage.Transform(x, y);
                    array.Add(new PdfReal(Math.Round(p.X, 3)));
                    array.Add(new PdfReal(Math.Round(p.Y, 3)));
                }
                return array;
            }
            annot.Set(PdfName.Subtype, PdfName.Get(shape.Kind switch
            {
                ShapeKind.Rectangle => "Square",
                ShapeKind.Ellipse => "Circle",
                ShapeKind.Line or ShapeKind.Arrow => "Line",
                _ => "Polygon",
            }));
            annot.Set(PdfName.Get("C"), shape.StrokeColor is { } stroke ? Color(stroke) : new PdfArray());
            if (shape.FillColor is { } fill && !shape.IsLine) annot.Set(PdfName.Get("IC"), Color(fill));
            var border = new PdfDictionary();
            border.Set(PdfName.Get("W"), new PdfReal(shape.StrokeColor is null ? 0 : shape.StrokeWidth));
            border.Set(PdfName.Get("S"), PdfName.Get(shape.Dashed ? "D" : "S"));
            if (shape.Dashed) border.Set(PdfName.Get("D"), new PdfArray([.. ElementAppearance.ShapeDash(shape).Select(d => (PdfObject)new PdfReal(Math.Round(d, 3)))]));
            annot.Set(PdfName.Get("BS"), border);
            if (shape.Opacity < 1) annot.Set(PdfName.Get("CA"), new PdfReal(Math.Round(shape.Opacity, 3)));
            if (shape.IsLine)
            {
                var (start, end) = ElementAppearance.ShapeLine(shape);
                annot.Set(PdfName.Get("L"), Points([start, end]));
                if (shape.Kind == ShapeKind.Arrow)
                {
                    annot.Set(PdfName.Get("LE"), new PdfArray([PdfName.Get("None"), PdfName.Get("ClosedArrow")]));
                    if (shape.StrokeColor is { } head) annot.Set(PdfName.Get("IC"), Color(head));
                }
            }
            else if (ElementAppearance.ShapeVertices(shape) is { Length: >= 3 } vertices)
            {
                annot.Set(PdfName.Get("Vertices"), Points(vertices));
            }
        }

        /// <summary>
        /// A dimension (Ablaufplan 7.6 f; docs/FEHLERGEDAECHTNIS.md, No. 587) as the annotation ISO 32000-2 has for it: a length as a line
        /// dimension (12.5.6.7: /L, /IT /LineDimension, arrows /LE, caption /Cap above the line), a polyline and an arc (finely divided, so that
        /// its length is that of the arc) as a polyline dimension, an area as a polygon dimension (12.5.6.9: /Vertices, /IT), each with its
        /// scale as /Measure (12.9) and its value as /Contents — other programs show it as measured and measure the same; an angle as a
        /// polyline without a scale. Colour /C (and /IC for arrows and fill), width /BS. A polyline or area with arcs (Ablaufplan 7.6 j): in
        /// PDF 2.0 with its curves as /Path instead of /Vertices (12.5.6.9: operands of m, l and c), below 2.0 its course finely divided as
        /// /Vertices — either way other programs measure the length of the arcs.
        /// </summary>
        private static void DimensionAnnotation(PdfDictionary annot, DimensionElement d, bool pdf20)
        {
            static PdfArray Color(int c) => new([new PdfReal(Math.Round(((c >> 16) & 0xFF) / 255.0, 4)), new PdfReal(Math.Round(((c >> 8) & 0xFF) / 255.0, 4)),
                new PdfReal(Math.Round((c & 0xFF) / 255.0, 4))]);
            static PdfArray Points(IEnumerable<(double X, double Y)> points)
            {
                var array = new PdfArray();
                foreach (var (x, y) in points)
                {
                    array.Add(new PdfReal(Math.Round(x, 3)));
                    array.Add(new PdfReal(Math.Round(y, 3)));
                }
                return array;
            }
            var points = d.PagePoints;
            var arrows = new PdfArray([PdfName.Get("ClosedArrow"), PdfName.Get("ClosedArrow")]);
            // the shape of a polyline or area with arcs: its curves (2.0) or its course
            var outline = d.ArcPoints.Count > 0 ? ElementAppearance.Outline(d) : null;
            var curved = outline is not null && outline.Pieces.Any(p => p.IsArc);
            void Shape()
            {
                if (!curved) annot.Set(PdfName.Get("Vertices"), Points(points));
                else if (!pdf20) annot.Set(PdfName.Get("Vertices"), Points(outline!.Path));
                else annot.Set(PdfName.Get("Path"), Dimensions.CurvePath(outline!, points[0]));
            }
            switch (d.Kind)
            {
                case MeasureKind.Length:
                    annot.Set(PdfName.Subtype, PdfName.Get("Line"));
                    annot.Set(PdfName.Get("L"), Points([points[0], points[^1]]));
                    annot.Set(PdfName.Get("IT"), PdfName.Get("LineDimension"));
                    annot.Set(PdfName.Get("LE"), arrows);
                    annot.Set(PdfName.Get("IC"), Color(d.Color));
                    annot.Set(PdfName.Get("Cap"), PdfBoolean.Of(true));
                    annot.Set(PdfName.Get("CP"), PdfName.Get("Top"));
                    // a caption the user moved: its offset from where it sits by itself (/CO, 12.5.6.7; No. 703) — other programs that lay the
                    // caption themselves put it there as well
                    if (d.PageLabelAt is { } label)
                    {
                        var home = ElementAppearance.CaptionHome(points[0], points[^1], d.StrokeWidth, out var along, out var across);
                        var (dx, dy) = (label.X - home.X, label.Y - home.Y);
                        annot.Set(PdfName.Get("CO"), new PdfArray([new PdfReal(Math.Round(dx * along.X + dy * along.Y, 3)),
                            new PdfReal(Math.Round(dx * across.X + dy * across.Y, 3))]));
                    }
                    break;
                case MeasureKind.Area:
                    annot.Set(PdfName.Subtype, PdfName.Get("Polygon"));
                    Shape();
                    annot.Set(PdfName.Get("IT"), PdfName.Get("PolygonDimension"));
                    if (d.FillOpacity > 0) annot.Set(PdfName.Get("IC"), Color(d.Color));
                    break;
                case MeasureKind.Polyline or MeasureKind.Radius:
                    annot.Set(PdfName.Subtype, PdfName.Get("PolyLine"));
                    if (d.Kind == MeasureKind.Radius && d.Path.Count >= 2) annot.Set(PdfName.Get("Vertices"), Points(d.PagePath));
                    else Shape();
                    annot.Set(PdfName.Get("IT"), PdfName.Get("PolyLineDimension"));
                    if (d.Kind == MeasureKind.Polyline)
                    {
                        annot.Set(PdfName.Get("LE"), new PdfArray([PdfName.Get("None"), PdfName.Get("ClosedArrow")]));
                        annot.Set(PdfName.Get("IC"), Color(d.Color));
                    }
                    break;
                default:
                    annot.Set(PdfName.Subtype, PdfName.Get("PolyLine"));
                    annot.Set(PdfName.Get("Vertices"), Points(points));
                    break;
            }
            annot.Set(PdfName.Get("C"), Color(d.Color));
            var border = new PdfDictionary();
            border.Set(PdfName.Get("W"), new PdfReal(Math.Round(d.StrokeWidth, 3)));
            border.Set(PdfName.Get("S"), PdfName.Get("S"));
            annot.Set(PdfName.Get("BS"), border);
            if (d.Measure is { } measure && d.Kind != MeasureKind.Angle) annot.Set(PdfName.Get("Measure"), measure.ToDictionary());
            annot.Set(PdfName.Contents, new PdfString(PdfTextEncoding.Encode(d.Detail.Length > 0 ? d.Text + "\n" + d.Detail : d.Text)));
        }

        /// <summary>
        /// Assemble the form anew: there are new fields or changed properties, or own fields of an earlier save are still in it
        /// (they are written anew with their current state, deleted ones are dropped). Not when flattening.
        /// </summary>
        private bool RewriteForm => !_options.Flatten && (_newFields.Count > 0 || _specChanged || _settled || OwnedAnnotations().Any(a => a.GetName(PdfName.Subtype)?.Value == "Widget"));

        /// <summary>
        /// Annotations of a page that are not taken over: own elements (they come anew), deleted foreign ones, those under
        /// redactions and, when sanitising, comments, file attachments and annotations on hidden layers.
        /// </summary>
        private IReadOnlySet<PdfDictionary> DroppedAnnotations(PageItem item)
        {
            var owned = ElementStore.For(item.Source, item.Page).Annotations;
            if (_foreignDeleted.TryGetValue(item, out var deleted))
                owned = new HashSet<PdfDictionary>(owned.Concat(deleted), ReferenceEqualityComparer.Instance);
            if (_redactionDrops.TryGetValue(item.Page.Dictionary, out var redacted))
                owned = new HashSet<PdfDictionary>(owned.Concat(redacted), ReferenceEqualityComparer.Instance);
            if (CleanupOf(item) == CleanupItems.None && !FlattensLayers || item.Page.Dictionary.Get(Annots) is not PdfArray annots) return owned;
            HashSet<PdfDictionary>? dropped = null;
            for (var i = 0; i < annots.Count; i++)
                if (annots.Get(i) is PdfDictionary annot && (DocumentCleanup.DropsAnnotation(annot, _options.Cleanup, KeptFor(item.Source), item.Source) ||
                    // Annotations on hidden layers: as hidden as their content (one that is printed stays, like its layer)
                    CleanupOf(item).HasFlag(CleanupItems.HiddenLayers) && _sources[item.Source].NeverShownContent.IsHidden(annot.Get(PdfName.Get("OC")), null) ||
                    // a copy without layers: as the display shows the annotation
                    FlattensLayers && _sources[item.Source].OptionalContent.IsHidden(annot.Get(PdfName.Get("OC")), null)))
                    (dropped ??= new HashSet<PdfDictionary>(owned, ReferenceEqualityComparer.Instance)).Add(annot);
            return dropped ?? owned;
        }

        /// <summary>Own annotations of all pages taken over (written anew as elements), plus those dropped by redaction or deletion.</summary>
        private HashSet<PdfDictionary> OwnedAnnotations()
        {
            if (_owned is not null) return _owned;
            _owned = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            foreach (var item in _pages) _owned.UnionWith(ElementStore.For(item.Source, item.Page).Annotations);
            foreach (var dropped in _redactionDrops.Values) _owned.UnionWith(dropped);
            _owned.UnionWith(_foreignRemoved);
            return _owned;
        }

        private HashSet<PdfDictionary>? _owned;

        /// <summary>
        /// Field and widget in one (12.7.4.3): text field with value and default appearance for programs that redraw it;
        /// signature field (/Sig) empty, so that other programs offer signing there.
        /// </summary>
        private PdfDictionary Widget(ElementAppearance appearance, int page)
        {
            var widget = new PdfDictionary();
            foreach (var (key, value) in appearance.Annotation.Entries) widget.Set(key, value);
            widget.Set(PdfName.Get("P"), new OutputReference(page));
            widget.Set(ElementStore.Key, ElementStore.Describe(appearance.Element, null));
            widget.Remove(ElementStore.LegacyKey);
            if (appearance.Element is SignatureFieldElement signatureField)
            {
                widget.Set(PdfName.Get("FT"), PdfName.Get("Sig"));
                widget.Set(T, new PdfString(PdfTextEncoding.Encode(FieldName(signatureField.FieldName, Tr.T("Unterschrift")))));
                // a neutral label for screen readers, without a name (docs/FEHLERGEDAECHTNIS.md, No. 493)
                Label(widget, Tr.T("Unterschrift"));
                return widget;
            }
            var element = (TextElement)appearance.Element;
            widget.Set(PdfName.Get("FT"), PdfName.Get("Tx"));
            widget.Set(T, new PdfString(PdfTextEncoding.Encode(FieldName(element.FieldName, Tr.T("Textfeld")))));
            if (FieldLabels.ScreenReaderLabel("", element.FieldName) is { } textLabel) Label(widget, textLabel);
            widget.Set(PdfName.Get("V"), new PdfString(PdfTextEncoding.Encode(element.Text)));
            var font = element.Font switch { ElementFont.Serif => "TiRo", ElementFont.Mono => "Cour", _ => "Helv" };
            var c = element.Color;
            var da = $"/{font} {ElementFace.Number(element.FontSize)} Tf {ElementFace.Number(((c >> 16) & 0xFF) / 255.0)} " +
                     $"{ElementFace.Number(((c >> 8) & 0xFF) / 255.0)} {ElementFace.Number((c & 0xFF) / 255.0)} rg";
            widget.Set(Da, new PdfString(System.Text.Encoding.ASCII.GetBytes(da)));
            if (element.Multiline) widget.Set(PdfName.Get("Ff"), PdfInteger.Of(1 << 12));
            if (element.Alignment != ElementAlignment.Left) widget.Set(PdfName.Get("Q"), PdfInteger.Of((int)element.Alignment));
            if (element.Rotation != 0)
            {
                var mk = new PdfDictionary();
                mk.Set(PdfName.Get("R"), PdfInteger.Of(element.Rotation));
                widget.Set(PdfName.Get("MK"), mk);
            }
            return widget;
        }

        // New radio groups by name: one field whose widgets are the radio buttons; new calculated fields for /CO
        private readonly Dictionary<string, (int Number, PdfDictionary Field, PdfArray Kids)> _radioGroups = new(StringComparer.Ordinal);
        private readonly List<(int Number, string Name, FieldCalculation Calc)> _newCalculated = [];
        private static readonly PdfName KidsKey = PdfName.Get("Kids"), ParentKey = PdfName.Get("Parent");

        /// <summary>
        /// New form field as a widget with an appearance (check boxes with on and off state) and the entries from its
        /// properties; field and widget in one, radio buttons as widgets of a shared field per name.
        /// </summary>
        /// <param name="labels">The own labels of the page (text elements with <see cref="TextElement.LabelFor"/>): the label of a check box or
        /// radio group and of each radio button, where it has no tooltip — for screen readers (ISO 14289-2, 8.10.2; docs/FEHLERGEDAECHTNIS.md, No. 636).</param>
        private PdfDictionary NewField(FieldElement element, ElementAppearance appearance, int page, int number, IReadOnlyList<TextElement>? labels = null)
        {
            var spec = element.Spec;
            string? OwnLabel(string state) => labels?.FirstOrDefault(t => t.LabelFor == spec.Name && t.LabelState == state && t.Text.Trim().Length > 0)?.Text.Trim();
            // what screen readers say: the tooltip, else the own label, else the name if it is no technical one
            var tip = spec.Tooltip.Trim().Length > 0 ? spec.Tooltip : OwnLabel("") ?? "";
            var widget = new PdfDictionary();
            widget.Set(PdfName.Type, PdfName.Get("Annot"));
            widget.Set(PdfName.Subtype, PdfName.Get("Widget"));
            widget.Set(RectKey, appearance.Annotation.Get(RectKey)!);
            widget.Set(PdfName.Get("P"), new OutputReference(page));
            spec.WriteWidget(widget, element.Rotation);
            var ap = new PdfDictionary();
            var on = ElementAppearance.OnStateOf(spec);
            var isOn = element.Value == on;
            if (appearance.States is { } states)
            {
                var normal = new PdfDictionary();
                foreach (var (name, form) in states) normal.Set(PdfName.Get(name), form);
                ap.Set(PdfName.N, normal);
                widget.Set(AS, PdfName.Get(isOn ? on : "Off"));
            }
            else ap.Set(PdfName.N, appearance.Form);
            widget.Set(AP, ap);

            // linked: a kid of the group's field, which carries name and value (PlanFieldLinks)
            if (_ownLinks.TryGetValue(element, out var link))
            {
                widget.Set(ParentKey, new OutputReference(link.Number));
                link.Kids.Add(new OutputReference(number));
                return widget;
            }

            if (spec.Kind == FormFieldKind.Radio)
            {
                if (!_radioGroups.TryGetValue(spec.Name, out var group))
                {
                    var field = new PdfDictionary();
                    (spec with { Name = FieldName(spec.Name, Tr.T("Optionsgruppe")) }).WriteField(field);
                    // the group's label for screen readers: its tooltip, else its name if it is no technical one (No. 493)
                    if (!field.ContainsKey(TuKey) && FieldLabels.ScreenReaderLabel(tip, spec.Name) is { } groupLabel)
                        field.Set(TuKey, new PdfString(PdfTextEncoding.Encode(groupLabel)));
                    field.Set(V, PdfName.Get("Off"));
                    var kids = new PdfArray();
                    field.Set(KidsKey, kids);
                    var groupNumber = NewNumber();
                    _memoryQueue.Enqueue((groupNumber, field));
                    _newFields.Add(groupNumber);
                    _radioGroups[spec.Name] = group = (groupNumber, field, kids);
                }
                widget.Set(ParentKey, new OutputReference(group.Number));
                group.Kids.Add(new OutputReference(number));
                if (isOn) group.Field.Set(V, PdfName.Get(on));
                // each button says which option it is (ISO 14289-2, 8.10.2.3 and 8.10.3.2.3: every widget of a field needs its own
                // label): the option text, as the form recognition gives it; else the group's label (No. 493)
                if ((OwnLabel(spec.ExportValue) ?? FieldLabels.ScreenReaderLabel("", spec.ExportValue) ?? FieldLabels.ScreenReaderLabel(tip, spec.Name)) is { } option)
                    widget.Set(PdfName.Contents, new PdfString(PdfTextEncoding.Encode(option)));
                return widget;
            }

            var fallback = spec.Kind switch
            {
                FormFieldKind.CheckBox => Tr.T("Kontrollkästchen"),
                FormFieldKind.ComboBox => Tr.T("Kombinationsfeld"),
                FormFieldKind.ListBox => Tr.T("Listenfeld"),
                FormFieldKind.PushButton => Tr.T("Schaltfläche"),
                FormFieldKind.Signature => Tr.T("Unterschrift"),
                _ => Tr.T("Textfeld"),
            };
            (spec with { Name = FieldName(spec.Name, fallback) }).WriteField(widget);
            // the label for screen readers (No. 493, 636): the tooltip, else the own label, else the name if it is no technical one — never „Text1“
            if (FieldLabels.ScreenReaderLabel(tip, spec.Name) is { } label) Label(widget, label);
            switch (spec.Kind)
            {
                case FormFieldKind.CheckBox:
                    widget.Set(V, PdfName.Get(isOn ? on : "Off"));
                    break;
                case FormFieldKind.ListBox when spec.MultiSelect && element.Value.Contains('\n'):
                    widget.Set(V, new PdfArray([.. element.Value.Split('\n').Take(10_000).Select(v => (PdfObject)new PdfString(PdfTextEncoding.Encode(v)))]));
                    break;
                case FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.ListBox when element.Value.Length > 0:
                    widget.Set(V, new PdfString(PdfTextEncoding.Encode(element.Value)));
                    break;
            }
            _newFields.Add(number);
            if (spec.Kind == FormFieldKind.Text && spec.Calculation is { IsActive: true } newCalc) _newCalculated.Add((number, spec.Name, newCalc));
            return widget;
        }

        private static readonly PdfName TuKey = PdfName.Get("TU");

        /// <summary>The label of a new field for screen readers: /TU (unless set) and /Contents of its widget (ISO 14289-1, 7.18.1; ISO 14289-2, 8.10.2.3).</summary>
        private static void Label(PdfDictionary widget, string label)
        {
            if (!widget.ContainsKey(TuKey)) widget.Set(TuKey, new PdfString(PdfTextEncoding.Encode(label)));
            widget.Set(PdfName.Contents, new PdfString(PdfTextEncoding.Encode(label)));
        }

        /// <summary>Unique field name: the requested one, otherwise the localised fallback with a number ("Textfeld1", "Textfeld2" … or "Unterschrift1" …).</summary>
        private string FieldName(string wanted, string fallback)
        {
            wanted = wanted.Trim();
            if (wanted.Length > 0 && _fieldNames.Add(wanted)) return wanted;
            var stem = wanted.Length > 0 ? wanted : fallback;
            // Continue counting where the stem last stopped (otherwise 500,000 attempts for 1,000 fields)
            var k = _fieldNameNext.GetValueOrDefault(stem, 1);
            while (!_fieldNames.Add($"{stem}{k}")) k++;
            _fieldNameNext[stem] = k + 1;
            return $"{stem}{k}";
        }

        private readonly Dictionary<string, int> _fieldNameNext = new(StringComparer.Ordinal);

        /// <summary>
        /// /Encrypt with everything it refers to put in directly. Its parts may be objects of their own (crypt filters as
        /// <c>/CF 7 0 R</c>); written last and unencrypted, such references would be lost or point to an encrypted object that no
        /// reader can use to find the key. See docs/FEHLERGEDAECHTNIS.md, No. 242.
        /// </summary>
        /// <remarks>
        /// A reference to an object that is being put in already (a cycle) becomes null, the copy goes only a few levels deep and has a budget
        /// of parts: in a damaged
        /// file the parts referred to each other again and again, and the copy grew exponentially up to the nesting limit until the memory
        /// ran out (docs/FEHLERGEDAECHTNIS.md, No. 378). A real /Encrypt has a few dozen.
        /// </remarks>
        private static PdfObject Inlined(PdfObject obj, PdfDocument doc)
        {
            var budget = InlineBudget;
            var open = new HashSet<int>();
            return Inline(obj, 0);

            PdfObject Inline(PdfObject o, int depth)
            {
                if (depth > InlineDepth || --budget < 0) return PdfNull.Instance;
                switch (o)
                {
                    case PdfReference r:
                        if (!open.Add(r.Id.Number)) return PdfNull.Instance;
                        var resolved = Inline(doc.GetObject(r.Id.Number), depth + 1);
                        open.Remove(r.Id.Number);
                        return resolved;
                    case PdfStream:
                        return PdfNull.Instance;   // the standard security handler has no streams
                    case PdfArray a:
                        var array = new PdfArray();
                        for (var i = 0; i < a.Count && budget >= 0; i++) array.Add(Inline(a[i], depth + 1));
                        return array;
                    case PdfDictionary d:
                        var dict = new PdfDictionary(d.Count);
                        foreach (var (key, value) in d.Entries)
                        {
                            if (budget < 0) break;
                            dict.Set(key, Inline(value, depth + 1));
                        }
                        return dict;
                    default:
                        return o;
                }
            }
        }

        /// <summary>The most parts <see cref="Inlined"/> puts in.</summary>
        private const int InlineBudget = 10_000;

        /// <summary>How deep <see cref="Inlined"/> goes: /Encrypt → /CF → a crypt filter → its values, a few levels; deeper is damage.</summary>
        private const int InlineDepth = 16;

        /// <summary>
        /// Object from memory (element) for the new file: arrays and dictionaries copied directly, streams and queued objects
        /// (fonts) as a reference, queued for writing the first time.
        /// </summary>
        private PdfObject Fresh(PdfObject obj, int depth = 0)
        {
            if (depth > PdfLimits.MaxNesting) return PdfNull.Instance;
            if (obj is PdfDictionary or PdfStream && _memoryNumbers.TryGetValue(obj, out var known)) return new OutputReference(known);
            switch (obj)
            {
                case PdfStream stream:
                    var number = NewNumber();
                    _memoryNumbers[stream] = number;
                    _memoryQueue.Enqueue((number, stream));
                    return new OutputReference(number);
                case PdfArray a:
                    var array = new PdfArray();
                    for (var i = 0; i < a.Count; i++) array.Add(Fresh(a[i], depth + 1));
                    return array;
                case PdfDictionary d:
                    // PDF 2.0: the standard fonts of fields (/DR) and elements as above (No. 372)
                    var entries = Pdf20 && StandardFontEmbedding.Described(d, _main) is { } described ? described : d;
                    var dict = new PdfDictionary(entries.Count);
                    foreach (var (key, value) in entries.Entries)
                        dict.Set(key, key == FontDescriptorKey && value is PdfDictionary descriptor ? IndirectDescriptor(descriptor) : Fresh(value, depth + 1));
                    return dict;
                case PdfReference:
                    return PdfNull.Instance;   // elements do not refer into files
                default:
                    return obj;
            }
        }

        private static readonly PdfName FontDescriptorKey = PdfName.Get("FontDescriptor");

        /// <summary>
        /// The form of an element's appearance for the new file, as built here (<see cref="Fresh"/>) — for a filled-in field of another program
        /// whose appearance was updated, what stays of the old one (<see cref="ElementAppearance.Frame"/>) as an object of its file, its references
        /// mapped to the copy (ISO 32000-2, 12.7.4.3, sentences 46–47; docs/FEHLERGEDAECHTNIS.md, No. 815).
        /// </summary>
        private PdfObject WrittenForm(ElementAppearance appearance)
        {
            if (appearance.Frame is not { } frame) return Fresh(appearance.Form);
            var form = appearance.Form;
            var dict = new PdfDictionary(form.Dictionary.Count);
            foreach (var (key, value) in form.Dictionary.Entries) dict.Set(key, value);
            var resources = new PdfDictionary();
            if (form.Dictionary.GetDictionary(PdfName.Resources) is { } own)
                foreach (var (key, value) in own.Entries) resources.Set(key, value);
            var xobjects = new PdfDictionary();
            xobjects.Set(ElementAppearance.FrameName, Rebase(frame, AddSource(appearance.Source)));
            resources.Set(XObject, xobjects);
            dict.Set(PdfName.Resources, resources);
            return Fresh(new PdfStream(dict, form.RawData ?? []));
        }

        /// <summary>
        /// PDF 2.0: standard fonts held directly in a font map of a dictionary as read — its own /Font (resources), that of its /Resources
        /// (pages, forms, appearances) or of its /DR (the form) — described as well (<see cref="StandardFontEmbedding.Described"/>; a map or font
        /// that is an object of its own is described when it is written). A copy, or null if nothing changes (docs/FEHLERGEDAECHTNIS.md, No. 372).
        /// </summary>
        private PdfDictionary? WithDescribedFontMaps(PdfDictionary dict, PdfDocument doc)
        {
            PdfDictionary? result = null;
            PdfDictionary? Described(PdfDictionary map)
            {
                if (map.GetRaw(Font) is not PdfDictionary fonts) return null;
                PdfDictionary? newFonts = null;
                foreach (var (key, value) in fonts.Entries)
                    if (value is PdfDictionary font && StandardFontEmbedding.Described(font, doc) is { } described)
                    {
                        described.Set(FontDescriptorKey, IndirectDescriptor((PdfDictionary)described.Get(FontDescriptorKey), font));
                        (newFonts ??= Clone(fonts)).Set(key, described);
                    }
                if (newFonts is null) return null;
                var copy = Clone(map);
                copy.Set(Font, newFonts);
                return copy;
            }
            if (Described(dict) is { } own) result = own;
            foreach (var holder in (ReadOnlySpan<PdfName>)[PdfName.Resources, Dr])
                if (dict.GetRaw(holder) is PdfDictionary map && Described(map) is { } newMap)
                    (result ??= Clone(dict)).Set(holder, newMap);
            return result;
        }

        /// <summary>
        /// PDF 2.0: a Type 2 CIDFont with an embedded program needs /CIDToGIDMap (ISO 32000-2, Table 115: required; in PDF 1.7 optional with the
        /// identity as default) — one without gets /Identity, what it meant before; also one held directly in /DescendantFonts. A copy, or null
        /// if nothing changes (docs/FEHLERGEDAECHTNIS.md, No. 524; found with the reference model of ISO 32000-2).
        /// </summary>
        private static PdfDictionary? WithCidToGidMap(PdfDictionary dict)
        {
            static bool Lacks(PdfDictionary font) =>
                font.GetName(PdfName.Subtype)?.Value == "CIDFontType2" && !font.ContainsKey(CidToGidMapKey) &&
                font.Get(FontDescriptorKey) is PdfDictionary descriptor &&
                (descriptor.ContainsKey(PdfName.Get("FontFile2")) || descriptor.ContainsKey(PdfName.Get("FontFile3")));
            if (Lacks(dict))
            {
                var copy = Clone(dict);
                copy.Set(CidToGidMapKey, PdfName.Get("Identity"));
                return copy;
            }
            if (dict.GetName(PdfName.Subtype)?.Value != "Type0" || dict.GetRaw(PdfName.Get("DescendantFonts")) is not PdfArray descendants) return null;
            if (!Enumerable.Range(0, descendants.Count).Any(i => descendants[i] is PdfDictionary direct && Lacks(direct))) return null;
            var changed = new PdfArray { Resolver = descendants.Resolver };
            for (var i = 0; i < descendants.Count; i++)
                if (descendants[i] is PdfDictionary direct && Lacks(direct))
                {
                    var font = Clone(direct);
                    font.Set(CidToGidMapKey, PdfName.Get("Identity"));
                    changed.Add(font);
                }
                else changed.Add(descendants[i]);
            var result = Clone(dict);
            result.Set(PdfName.Get("DescendantFonts"), changed);
            return result;
        }

        private static readonly PdfName CidToGidMapKey = PdfName.Get("CIDToGIDMap");

        /// <summary>The file is written as PDF 2.0 (or later).</summary>
        private bool Pdf20 => CompareVersions(_version, "2.0") >= 0;

        /// <summary>Every annotation of some area needs an appearance: PDF/A (ISO 19005-2, 6.3.3) and PDF 2.0 (ISO 32000-2, 12.5.5).</summary>
        private bool AppearancesRequired => _pdfA is not null || Pdf20;

        /// <summary>Font descriptors written as objects of their own, by the dictionary they were made from (one number each).</summary>
        private readonly Dictionary<PdfDictionary, int> _descriptors = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// A font descriptor held directly in a font dictionary (built in memory: fonts of elements, fields, the text layer, placeholders of a
        /// redaction): written as an object of its own — ISO 32000-2, 9.8.1 requires an indirect reference (docs/FEHLERGEDAECHTNIS.md, No. 362).
        /// </summary>
        /// <param name="descriptor">The descriptor to write (already converted for the output when it came from a source).</param>
        /// <param name="original">The dictionary it was made from (the same descriptor is written once); itself if not converted.</param>
        private OutputReference IndirectDescriptor(PdfDictionary descriptor, PdfDictionary? original = null)
        {
            original ??= descriptor;
            if (_descriptors.TryGetValue(original, out var known)) return new OutputReference(known);
            var number = NewNumber();
            _descriptors[original] = number;
            _memoryQueue.Enqueue((number, descriptor));
            return new OutputReference(number);
        }

        /// <summary>
        /// Stream in memory that refers into a source (redacted content, new images and forms of the <see cref="Redactor"/>):
        /// a new number; when it is written, its references are converted via the source.
        /// </summary>
        private OutputReference Sourced(PdfStream stream, Source source)
        {
            if (_memoryNumbers.TryGetValue(stream, out var known)) return new OutputReference(known);
            var number = NewNumber();
            _memoryNumbers[stream] = number;
            _sourcedQueue.Enqueue((number, stream, source));
            return new OutputReference(number);
        }

        private readonly Queue<(int Number, PdfStream Stream, Source Source)> _sourcedQueue = new();

        /// <summary>Writes everything queued until nothing more follows (the paths can queue new objects for each other).</summary>
        private void DrainAll()
        {
            while (_queue.Count > 0 || _memoryQueue.Count > 0 || _sourcedQueue.Count > 0 || _liftedQueue.Count > 0)
            {
                Drain();
                DrainMemory();
                // direct file specifications and collection items taken out of their objects (Table 43), with the source of those objects
                while (_liftedQueue.TryDequeue(out var lifted)) WriteObject(lifted.Number, lifted.Dict, lifted.Source);
                while (_sourcedQueue.TryDequeue(out var entry))
                {
                    var (number, stream, source) = entry;
                    var dict = (PdfDictionary)Rebase(stream.Dictionary, source);
                    dict.Remove(PdfName.Length);
                    var data = stream.RawData ?? [];
                    // a further copy of a form with keys of its own: no keys, its marked content artifact content (6.6 a, No. 647)
                    if (_artifactCopies.Contains(stream)) (dict, data) = AsArtifactCopy(dict, data, stream.Dictionary.GetDictionary(PdfName.Resources));
                    // page content whose emptied marked content lost its element: artifact content (6.6 b, No. 649)
                    if (_emptiedArtifacts.TryGetValue(stream, out var emptied) && !dict.ContainsKey(PdfName.Filter))
                        data = Accessibility.MarkedContentIds.AsArtifacts(data, emptied.Mcids, emptied.Properties);
                    // bars within text: each in the marked content of its Artifact element (6.3 step 6, No. 659)
                    if (_inlineBars.TryGetValue(stream, out var inline) && !dict.ContainsKey(PdfName.Filter)) data = WithInlineBars(data, inline);
                    var compress = _options.CompressStreams && !dict.ContainsKey(PdfName.Filter) && data.Length >= 64;
                    WriteStreamData(number, dict, compress ? Compress(data) : (ReadOnlySpan<byte>)data, compress, _security is not null, null);
                }
            }
        }

        /// <summary>
        /// An object with the resources its Type 3 fonts borrow (<see cref="_borrowed"/>), the object itself or a font in its direct parts —
        /// page → /Resources → /Font → font, or a graphics state's /Font, a few levels deep. The object itself when nothing changes.
        /// </summary>
        private PdfObject WithLentResources(PdfObject obj, Source? source, int depth)
        {
            if (depth > 8) return obj;
            switch (obj)
            {
                case PdfDictionary dict:
                {
                    PdfDictionary? copy = null;
                    // (resources naming nothing count as none, as the display reads them: S-A A67)
                    if (_borrowed.TryGetValue(dict, out var lent) && dict.GetDictionary(PdfName.Resources) is null)
                    {
                        copy = CloneWithResolver(dict);
                        // the object itself: the resources mapped to the copy (they name it by reference); a font inside it: an object of their
                        // own. Mapped, not as they are: on a redacted page they are built in memory — a pattern there has no number and went out
                        // as a dictionary without its data (docs/FEHLERGEDAECHTNIS.md, No. 787)
                        copy.Set(PdfName.Resources, depth == 0 ? Lent(lent, source) : LentObject(lent, source));
                    }
                    List<(PdfName Key, PdfObject Value)>? changed = null;
                    foreach (var (key, item) in dict.Entries)
                        if (item is PdfDictionary or PdfArray && WithLentResources(item, source, depth + 1) is var walked && !ReferenceEquals(walked, item))
                            (changed ??= []).Add((key, walked));
                    if (changed is not null)
                    {
                        copy ??= CloneWithResolver(dict);
                        foreach (var (key, walked) in changed) copy.Set(key, walked);
                    }
                    return copy ?? dict;
                }
                case PdfArray array:
                {
                    List<PdfObject>? items = null;
                    for (var i = 0; i < array.Count && i < 10_000; i++)
                    {
                        if (array[i] is not (PdfDictionary or PdfArray)) continue;
                        var walked = WithLentResources(array[i], source, depth + 1);
                        if (ReferenceEquals(walked, array[i])) continue;
                        if (items is null)
                        {
                            items = new List<PdfObject>(array.Count);
                            for (var k = 0; k < array.Count; k++) items.Add(array[k]);
                        }
                        items[i] = walked;
                    }
                    return items is null ? array : new PdfArray(items) { Resolver = array.Resolver };
                }
                default:
                    return obj;
            }
        }

        /// <summary>
        /// Resources lent to an object written with <paramref name="source"/>, mapped to the copy: references of the source to the new numbers,
        /// streams built in memory (the resources of a redacted page) to objects of their own. Before, they went in as they were — a pattern of a
        /// redacted page was written as a dictionary without its data, and the form drew grey (docs/FEHLERGEDAECHTNIS.md, No. 787). Written
        /// without a source, they are mapped with the source they were planned for (<see cref="_lentSource"/>), else as objects from memory —
        /// never as they are (No. 903).
        /// </summary>
        private PdfObject Lent(PdfDictionary lent, Source? source) =>
            (source ?? _lentSource.GetValueOrDefault(lent)) is { } from ? Rebase(lent, from) : Fresh(lent);

        /// <summary>The source each set of lent resources was planned for (<see cref="PlanResourceVariants"/>), by reference.</summary>
        private readonly Dictionary<PdfDictionary, Source> _lentSource = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Resources lent to a Type 3 font written directly, as an object of their own, once per resources and source: the page's resources
        /// hold that very font — given directly, the font would hold itself without end (No. 782).
        /// </summary>
        private OutputReference LentObject(PdfDictionary lent, Source? source)
        {
            if (_lentObjects.TryGetValue((lent, source), out var known)) return new OutputReference(known);
            var number = NewNumber();
            _lentObjects[(lent, source)] = number;   // before rebasing: the font inside names this number
            _memoryQueue.Enqueue((number, Lent(lent, source)));
            return new OutputReference(number);
        }

        private readonly Dictionary<(PdfDictionary, Source?), int> _lentObjects = new(LentKeyComparer.Instance);

        /// <summary>Resources and source compared by reference.</summary>
        private sealed class LentKeyComparer : IEqualityComparer<(PdfDictionary Lent, Source? Source)>
        {
            public static readonly LentKeyComparer Instance = new();
            public bool Equals((PdfDictionary Lent, Source? Source) x, (PdfDictionary Lent, Source? Source) y) =>
                ReferenceEquals(x.Lent, y.Lent) && ReferenceEquals(x.Source, y.Source);
            public int GetHashCode((PdfDictionary Lent, Source? Source) key) =>
                HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Lent), key.Source is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Source));
        }

        /// <summary>Writes queued objects from memory (which can queue further ones).</summary>
        private void DrainMemory()
        {
            while (_memoryQueue.TryDequeue(out var entry))
            {
                var (number, obj) = entry;
                if (obj is not PdfStream stream)
                {
                    WriteObject(number, Fresh(obj), null);
                    continue;
                }
                var dict = (PdfDictionary)Fresh(stream.Dictionary);
                dict.Remove(PdfName.Length);
                // a form built here names its resources, even none (ISO 32000-2, 8.10.1, Table 93; ISO 19005-2, 6.2.2) — the empty
                // appearances of the PDF/A step had none (docs/FEHLERGEDAECHTNIS.md, No. 366)
                if (dict.GetName(PdfName.Subtype)?.Value == "Form" && !Present(dict, PdfName.Resources)) dict.Set(PdfName.Resources, new PdfDictionary());
                var data = stream.RawData ?? [];
                // XMP stays readable without unpacking (as for metadata streams of the sources)
                var compress = _options.CompressStreams && !dict.ContainsKey(PdfName.Filter) && data.Length >= 64 && !dict.IsType(PdfName.Metadata);
                WriteStreamData(number, dict, compress ? Compress(data) : (ReadOnlySpan<byte>)data, compress, _security is not null, null);
            }
        }

        // ------------------------------------------------------------ Writing

        private void WriteHeader()
        {
            _scratch.Clear();
            _scratch.Ascii($"%PDF-{_version}\n%");
            _scratch.Add([0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
            Emit(_scratch.Written);
        }

        private void Emit(ReadOnlySpan<byte> bytes)
        {
            _position += bytes.Length;
            // the part with the trailer is held back until the MAC is in it (No. 557)
            if (_tail is not null)
            {
                _tail.Write(bytes);
                return;
            }
            _macHash?.AppendData(bytes);
            _output.Write(bytes);
        }

        // ------------------------------------------------------------ MAC (ISO/TS 32004; docs/FEHLERGEDAECHTNIS.md, No. 557)

        /// <summary>A fresh MAC is written into the copy (see <see cref="PdfSaveOptions.WriteMac"/>).</summary>
        private bool _writeMac;

        /// <summary>SHA-256 over everything written before the cross-references.</summary>
        private IncrementalHash? _macHash;

        /// <summary>The cross-references and trailer, held back while the MAC is computed.</summary>
        private MemoryStream? _tail;

        /// <summary>
        /// Completes the MAC (ISO/TS 32004, 6.5.1 and 6.6.2): /ByteRange from 0 to the end of the file around the /MAC gap (padded with spaces,
        /// the same width), the digest of those bytes, the token as hexadecimal digits exactly in the gap; then the held-back part is written.
        /// </summary>
        private void FinishMac()
        {
            var tail = _tail!.ToArray();
            _tail = null;
            var salt = _encryptDict!.GetString(PdfName.Get("KDFSalt"))!.Bytes;
            PdfMac.FillStandalone(tail, _position - tail.Length, _macHash!, _security!.MacKek(salt));
            _output.Write(tail);
            _macHash!.Dispose();
            _macHash = null;
        }

        /// <param name="source">Source the object comes from (its references are converted); null = assembled by the writer.</param>
        /// <summary>
        /// PDF 2.0 (not PDF/A, which has its own): forms, tiling patterns and Type 3 fonts without resources of their own, with those they are drawn
        /// with — dictionaries as read, compared by reference; the values are objects of the source (ISO 32000-2, 7.8.3; docs/FEHLERGEDAECHTNIS.md,
        /// No. 549).
        /// </summary>
        private readonly Dictionary<PdfDictionary, PdfDictionary> _borrowed = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// The content streams of the pages written (not for PDF/A, which writes every content stream anew): their numbers are written in the
        /// form of ISO 32000-2, 7.3.3 (S-A A01, docs/FEHLERGEDAECHTNIS.md, No. 930).
        /// </summary>
        private readonly ContentStreams _contentStreams = new();

        private void WriteObject(int number, PdfObject obj, Source? source)
        {
            // a copy of an owner with the resources of its set; names in resources that stand for such a copy (No. 895)
            obj = WithVariantNames(obj);
            if (_variantLent.TryGetValue(number, out var variantLent) && obj is PdfDictionary variantOwner && variantOwner.GetDictionary(PdfName.Resources) is null)
            {
                var withResources = CloneWithResolver(variantOwner);
                withResources.Set(PdfName.Resources, Lent(variantLent, source));
                obj = withResources;
            }
            // a Type 3 font that borrows its resources — an object of its own or written directly into a /Font dictionary (No. 782; before,
            // the direct one was left out, No. 549)
            if (_borrowed.Count > 0) obj = WithLentResources(obj, source, 0);
            // PDF/A first: it knows fonts, resources and the rest by the dictionaries as read — after the keys of the version were taken out
            // (in 2.0 the /Name of a font), the copy was another object, and a font the copy shows stayed without a program
            // (docs/FEHLERGEDAECHTNIS.md, No. 922)
            if (_pdfA is not null && obj is not PdfStream && number != _catalogNumber && number != _infoNumber) obj = PdfAFix(obj, source, number);
            // keys the version written does not have, or no longer (docs/FEHLERGEDAECHTNIS.md, No. 702)
            obj = VersionKeys.Filter(obj, _version, _pdfA is not null, Needs, TaggedCopy());
            // file specifications as ISO 32000-2, Table 43 asks of writers — after PDF/A, which judges embedded files by the objects as read
            // (docs/FEHLERGEDAECHTNIS.md, No. 780); a stream's dictionary in WriteStream
            if (obj is not PdfStream) obj = PartsAsWritten(obj, source);
            // PDF 2.0: a standard font that is not embedded with the entries every simple font needs there — after PDF/A, which embeds the
            // fonts text is shown with (docs/FEHLERGEDAECHTNIS.md, No. 372)
            if (Pdf20 && obj is PdfDictionary font && StandardFontEmbedding.Described(font, source?.Doc ?? _main) is { } described)
            {
                described.Set(FontDescriptorKey, IndirectDescriptor((PdfDictionary)described.Get(FontDescriptorKey), font));   // indirect, as everywhere (No. 362)
                obj = described;
            }
            else if (Pdf20 && _pdfA is null && obj is PdfDictionary holder && WithDescribedFontMaps(holder, source?.Doc ?? _main) is { } withFonts) obj = withFonts;
            // PDF 2.0: every annotation of some area has an appearance (ISO 32000-2, 12.5.5; PDF/A has seen to it already; No. 373)
            else if (Pdf20 && _pdfA is null && obj is PdfDictionary annot && WithAppearance(annot, source) is { } drawn) obj = drawn;
            // PDF 2.0: an embedded Type 2 CIDFont names its CID → glyph mapping (No. 524)
            if (Pdf20 && obj is PdfDictionary cidHolder && WithCidToGidMap(cidHolder) is { } mapped) obj = mapped;
            if (obj is PdfStream stream)
            {
                WriteStream(number, stream, source);
                return;
            }
            // Encrypted: catalogue and Info are not put into an object stream; the standard allows it, but a widely used reader otherwise
            // reports the file as damaged (see docs/FEHLERGEDAECHTNIS.md, No. 33); the encryption dictionary must never go in anyway
            // a signature dictionary neither: in an encrypted object stream its plain /Contents would be encrypted with the stream (No. 542)
            if (_objectStreams && number != _encryptNumber && !(_security is not null && (number == _catalogNumber || number == _infoNumber
                    || obj is PdfDictionary signatureDict && PdfSerializer.IsSignatureDictionary(signatureDict))))
            {
                AddToObjectStream(number, obj, source);
                return;
            }
            WriteDirect(number, obj, source, encrypt: true);
        }

        private Func<byte[], byte[]>? StringEncryption(int number, bool encrypt)
        {
            if (!encrypt || _security is not { } security) return null;
            var id = new PdfObjectId(number, 0);
            return bytes => security.EncryptString(id, bytes);
        }

        private void WriteDirect(int number, PdfObject obj, Source? source, bool encrypt)
        {
            _xref[number] = (1, _position, 0);
            _scratch.Clear();
            _scratch.Int(number);
            _scratch.Ascii(" 0 obj\n");
            PdfSerializer.Write(_scratch, obj, source?.Mapper ?? NoReferences, StringEncryption(number, encrypt));
            _scratch.Ascii("\nendobj\n");
            Emit(_scratch.Written);
        }

        /// <summary>Objects assembled by the writer contain only references of the new file.</summary>
        private static int NoReferences(PdfObjectId id) => 0;

        private void AddToObjectStream(int number, PdfObject obj, Source? source)
        {
            if (_osNumber == 0)
            {
                _osNumber = NewNumber();
                _osCount = 0;
            }
            _xref[number] = (2, _osNumber, _osCount);
            _osHeader.Int(number);
            _osHeader.Add((byte)' ');
            _osHeader.Int(_osBody.Length);
            _osHeader.Add((byte)' ');
            // Strings in object streams are not encrypted individually; the whole stream is
            PdfSerializer.Write(_osBody, obj, source?.Mapper ?? NoReferences, null);
            _osBody.Add((byte)'\n');
            _osCount++;
            if (_osCount >= ObjectStreamLimit || _osBody.Length >= ObjectStreamBytes) FlushObjectStream();
        }

        private void FlushObjectStream()
        {
            if (_osCount == 0) return;
            var dict = new PdfDictionary();
            dict.Set(PdfName.Type, PdfName.ObjStm);
            dict.Set(PdfName.N, PdfInteger.Of(_osCount));
            dict.Set(PdfName.First, PdfInteger.Of(_osHeader.Length));
            var number = _osNumber;
            // Pack header and body directly one after the other (no joining copy before packing and no copy afterwards)
            var packed = Compress(_osHeader.Written, _osBody.Written);
            _osNumber = 0;
            _osCount = 0;
            _osHeader.Clear();
            _osBody.Clear();
            WriteStreamData(number, dict, packed, compressed: true, encrypt: _security is not null, null);
        }

        /// <summary>
        /// Whether a metadata stream is the one of the document as written — the /Metadata of the output catalogue (from the main source, or
        /// built by the writer, as the PDF/A or slimmed XMP): the only one /EncryptMetadata false leaves plain (ISO 32000-2, Table 20;
        /// docs/FEHLERGEDAECHTNIS.md, No. 551).
        /// </summary>
        private bool IsDocumentXmp(int number, PdfDictionary dict, Source? source) =>
            dict.IsType(PdfName.Metadata) && (source is null
                || source.IsMain && _main.Catalog?.GetRaw(PdfName.Metadata) is PdfReference xmp && source.Map.TryGetValue(xmp.Id.Number, out var written) && written == number);

        /// <summary>The content in a form ISO 32000-2 allows (<see cref="ContentConformance.CanonicalSyntax"/>: numbers, unfinished arrays and
        /// dictionaries); null where it is, or where the content cannot be read or is damaged — it is then copied as it is: written anew, a
        /// damaged stream would lose the bytes behind the damage (docs/FEHLERGEDAECHTNIS.md, No. 937).</summary>
        private static byte[]? CanonicalContent(PdfStream stream, PdfDocument doc, out bool intact)
        {
            intact = false;
            try
            {
                // decoded into a rented buffer: every content stream of the file passes here (docs/FEHLERGEDAECHTNIS.md, No. 936)
                var raw = doc.GetRawStreamData(stream);
                using var data = StreamFilters.DecodePooled(raw, stream.Dictionary, out var damaged);
                intact = !damaged;   // read without damage: nothing to repair below
                // damage with bytes behind it: copied as stored (No. 937); a Flate stream the decoder read to its end (only its header or sum
                // not as RFC 1950 asks) is complete and written anew like any other (decided on 19.11.2026; No. 963)
                if (damaged && !(StreamFilters.Chain(stream.Dictionary) is [("FlateDecode", _)] && Flate.Repaired(raw) is not null)) return null;
                return ContentConformance.CanonicalSyntax(data.Array, data.Length);
            }
            catch (Exception e) when (e is PdfException or InvalidDataException or IOException)
            {
                return null;
            }
        }

        private void WriteStream(int number, PdfStream stream, Source? source)
        {
            var original = stream.Dictionary;
            // (PDF/A embeds the fonts it shows by the dictionaries as read and describes the rest itself: not there)
            var dict = Pdf20 && _pdfA is null && source is not null && WithDescribedFontMaps(original, source.Doc) is { } withFonts ? withFonts : Clone(original);
            dict.Remove(PdfName.Length);
            PartsWithin(dict, source);   // an external file named with a file specification dictionary (No. 780)
            // /DL names the decoded length (ISO 32000-2, 7.3.8.2, Table 5); only the file's own data can vouch for it: a stream built in memory
            // (edited content, redacted images) keeps no old value, nor does one re-encoded below (docs/FEHLERGEDAECHTNIS.md, No. 726)
            if (stream.RawData is not null) dict.Remove(DecodedLength);
            // (as read: resources naming nothing count as none, S-A A67)
            // mapped to the copy: on a redacted page they are built in memory (No. 787); without what the version does not have, as the rest of
            // the dictionary, which VersionKeys saw before they were lent — in 2.0 the page's /ProcSet came along (docs/FEHLERGEDAECHTNIS.md, No. 814)
            if (_variantLent.TryGetValue(number, out var variantLent) && original.GetDictionary(PdfName.Resources) is null)
                dict.Set(PdfName.Resources, VersionKeys.Filter(Lent(variantLent, source), _version, _pdfA is not null, Needs, TaggedCopy()));   // a copy of its set (No. 895)
            else if (_borrowed.Count > 0 && _borrowed.TryGetValue(original, out var lent) && original.GetDictionary(PdfName.Resources) is null)
                dict.Set(PdfName.Resources, VersionKeys.Filter(Lent(lent, source), _version, _pdfA is not null, Needs, TaggedCopy()));
            // a form with /PieceInfo carries /LastModified (ISO 32000-2, 8.10.2, Table 93) — the time of writing, as for pages (No. 755, 766)
            if (Present(dict, PieceInfoKey) && !Present(dict, LastModifiedKey) && original.GetName(PdfName.Subtype)?.Value == "Form")
                dict.Set(LastModifiedKey, new PdfString(System.Text.Encoding.ASCII.GetBytes(PdfDate(Modified))));
            // PDF 2.0: an embedded file names its kind as a MIME type — required where it is an associated file (ISO 32000-2, 7.11.4, Table 44);
            // a PDF as such, anything else as unknown data, as PDF/A does it (PdfAStream; docs/FEHLERGEDAECHTNIS.md, No. 375)
            if (Pdf20 && _pdfA is null && source is not null && original.GetName(PdfName.Type)?.Value == "EmbeddedFile" && original.GetName(PdfName.Subtype) is null)
                dict.Set(PdfName.Subtype, PdfName.Get(EmbeddedData(stream, source).AsSpan().StartsWith("%PDF-"u8) ? "application/pdf" : "application/octet-stream"));

            // A crypt filter of its own (/Crypt first, ISO 32000-2, 7.4.10): kept only where the encryption is kept and knows it (/Identity:
            // stored unencrypted); otherwise the filter goes and the data is written as any other stream — a named filter in a file without
            // /Encrypt, or one the new protection does not know, left the stream unreadable (docs/FEHLERGEDAECHTNIS.md, No. 442). A filter of
            // its own with V2 or AESV2 is never written either: Table 14 asks for the file key unchanged there, readers and writers take the key
            // per object (Algorithm 1) — the stream goes to the default filter of the document, read as before (decided on
            // 20.11.2026, S-B B46; docs/FEHLERGEDAECHTNIS.md, No. 991). Identity and AES-256 (no key per object) stay as they are.
            var chain = StreamFilters.Chain(original);
            PdfCipher? ownCipher = null;
            if (chain is [("Crypt", var cryptParms), ..])
            {
                // where the document's default filter for the stream does not encrypt (only some streams are), the stream keeps its filter of
                // its own as before: written without it, it would lie in the copy unencrypted
                var fallback = _security is not { } protection ? PdfCipher.None
                    : protection.EmbeddedFileCipher != protection.StreamCipher && source is not null && IsEmbeddedFileStream(stream, source)
                        ? protection.EmbeddedFileCipher : protection.StreamCipher;
                if (_keepsFilters && _security?.CipherNamed(cryptParms?.GetName(PdfName.Name)) is { } own &&
                    (own is not (PdfCipher.Rc4 or PdfCipher.Aes128) || fallback == PdfCipher.None))
                    ownCipher = own;
                else RemoveFirstFilter(dict);
            }
            var identityCrypt = ownCipher is not null;   // the stream keeps its crypt filter: its filters are not rewritten
            var cipher = _security is not { } security ? PdfCipher.None
                : ownCipher ?? (!security.EncryptMetadata && IsDocumentXmp(number, original, source) ? PdfCipher.None
                    : security.EmbeddedFileCipher != security.StreamCipher && source is not null && IsEmbeddedFileStream(stream, source)
                        ? security.EmbeddedFileCipher : security.StreamCipher);
            var encrypt = cipher != PdfCipher.None;
            // an attachment encrypted with a key that is missing (only the attachments are encrypted, not unlocked): its bytes as stored —
            // possible only where the encryption is kept and the method needs no key per object (AES-256); never silently destroyed
            if (source is not null && source.Doc.Security is { AttachmentsLocked: true } && source.Doc.StreamCipherFor(stream) is var stored && stored != PdfCipher.None)
            {
                if (!(_keepsFilters && ReferenceEquals(source.Doc, _options.EncryptionFrom ?? _main) && stored == cipher && cipher is PdfCipher.Aes256 or PdfCipher.Aes256Gcm))
                    throw new PdfAttachmentsLockedException(Tr.T("Ein Anhang ist mit eigenem Passwort verschlüsselt und lässt sich so nicht übernehmen. Bitte die Datei mit dem Passwort der Anhänge öffnen."));
                var asStored = Clone(original);
                asStored.Remove(PdfName.Length);
                WriteStreamData(number, asStored, source.Doc.StoredStreamData(stream), compressed: false, encrypt: false, source);
                return;
            }

            var compress = _options.CompressStreams && chain.Count == 0 && !original.ContainsKey(PdfName.F) &&
                           !original.IsType(PdfName.Metadata) && stream.DataLength >= 64;
            if (source is null)
            {
                WriteStreamData(number, dict, stream.RawData ?? [], compressed: false, encrypt, null, cipher);
                return;
            }

            if (_options.Compression is not null) _cancel.ThrowIfCancellationRequested();   // per stream: repacking images takes time
            (_decoded, _plainPacked) = (null, null);
            var doc = source.Doc;
            if (!identityCrypt && WithoutSharedGlobals(stream, doc, original) is { } unshared)
            {
                WriteStreamData(number, unshared.Dict, unshared.Data, compressed: true, encrypt, source, cipher);
                return;
            }
            // Stored unencrypted: bytes directly from the (memory-mapped) source, without a copy
            ReadOnlySpan<byte> raw = doc.IsStoredPlain(stream) ? doc.StoredStreamData(stream) : doc.GetRawStreamData(stream);
            if (_pdfA is not null && !identityCrypt && PdfAStream(stream, source, dict) is { } plain)
            {
                // PDF/A: LZW unpacked and packed with Flate, content written anew, borrowed resources given
                plain.Dict.Remove(DecodedLength);
                WriteStreamData(number, plain.Dict, plain.Compress ? Compress(plain.Data) : plain.Data, plain.Compress, encrypt, source, cipher);
                return;
            }
            // a content stream with numbers not written as 7.3.3 allows („--24“, „1.2.3“) or an array or dictionary an operator ends: those
            // parts as the display reads them, the rest byte for byte — the copy is well-formed, and other readers show what PDF-Knead showed
            // (S-A A01, A02; No. 930, 931)
            var intact = false;
            // a form, pattern, group or appearance with an inline image the display reads only with a tolerance: the image as an XObject in
            // the resources it draws with, now its own (decided on 20.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 993)
            if (_pdfA is null && !identityCrypt && !original.ContainsKey(PdfName.Get("F")) && _contentStreams.Contains(doc, stream) &&
                FormInlineImages(stream, doc, source) is var (formContent, formResources))
            {
                dict.Remove(PdfName.Filter);
                dict.Remove(PdfName.DecodeParms);
                dict.Remove(DecodedLength);
                dict.Set(PdfName.Resources, formResources);
                WriteStreamData(number, dict, Compress(formContent), compressed: true, encrypt, source, cipher);
                return;
            }
            if (_pdfA is null && !identityCrypt && !original.ContainsKey(PdfName.Get("F")) && _contentStreams.Contains(doc, stream) &&
                CanonicalContent(stream, doc, out intact) is { } canonical)
            {
                dict.Remove(PdfName.Filter);
                dict.Remove(PdfName.DecodeParms);
                dict.Remove(DecodedLength);
                WriteStreamData(number, dict, Compress(canonical), compressed: true, encrypt, source, cipher);
                return;
            }
            if (!identityCrypt && SubsetFont(stream, source, original) is { } font)
            {
                // Compression: unused glyphs emptied
                font.Dict.Remove(PdfName.Length);
                font.Dict.Remove(DecodedLength);
                WriteStreamData(number, font.Dict, font.Data, compressed: false, encrypt, source, cipher);
                return;
            }
            if (!identityCrypt && RewriteImage(stream, source, original, raw) is { } image)
            {
                // Compression: image downsampled or re-encoded (JPEG, 1-bit, palette, Flate)
                image.Dict.Remove(PdfName.Length);
                image.Dict.Remove(DecodedLength);
                WriteStreamData(number, image.Dict, image.Data, compressed: false, encrypt, source, cipher);
                return;
            }
            if (!compress && !identityCrypt && !original.ContainsKey(PdfName.F) && Repack(stream, source, original, raw) is { } repacked)
            {
                // Compression: repacked, smaller than before
                dict.Remove(PdfName.DecodeParms);
                if (repacked.Filter is { } filter) dict.Set(PdfName.Filter, filter);
                else dict.Remove(PdfName.Filter);
                if (repacked.Parms is { } newParms) dict.Set(PdfName.DecodeParms, newParms);
                WriteStreamData(number, dict, repacked.Data, compressed: false, encrypt, source, cipher);
                return;
            }
            if (!identityCrypt && !original.ContainsKey(PdfName.F) && _options.Compression?.Brotli != true && WithoutBrotli(original, raw) is { } flate)
            {
                // Brotli is not part of ISO 32000-2: only where the compressor was told to use it, otherwise packed with Flate so that every
                // reader opens the file (docs/FEHLERGEDAECHTNIS.md, No. 370)
                dict.Remove(PdfName.Filter);
                dict.Remove(PdfName.DecodeParms);
                if (flate.Filter is { } imageFilter) dict.Set(PdfName.Filter, imageFilter);
                if (flate.Parms is { } imageParms) dict.Set(PdfName.DecodeParms, imageParms);
                WriteStreamData(number, dict, flate.Filter is null ? Compress(flate.Data) : flate.Data, flate.Filter is null, encrypt, source, cipher);
                return;
            }
            // an image the display reads only with a tolerance — bytes before the JPEG start, a bare JPEG 2000 codestream, /Width and /Height not
            // those of the JPEG 2000 data: written as ISO 32000-2 asks, nothing lost (decided on 20.11.2026; S-A A14, A75, A76;
            // docs/FEHLERGEDAECHTNIS.md, No. 992) — the opacity channel /SMaskInData announces for a bare codestream named in the wrapper (No. 996)
            if (_pdfA is null && !identityCrypt && !original.ContainsKey(PdfName.F) && original.GetName(PdfName.Subtype)?.Value == "Image" &&
                ImageDataConformance.Repair(dict, raw, ImageDataConformance.BareOpacity(original, raw, source?.Doc)) is { } conformingImage)
            {
                WriteStreamData(number, dict, conformingImage, compressed: false, encrypt, source, cipher);
                return;
            }
            // a Flate stream the decoder read to its end, whose header (a window above 32 KB) or Adler-32 sum (missing, wrong) is not as RFC 1950
            // asks: packed anew as it asks, nothing is lost — damage with bytes behind it stays as stored (decided on 19.11.2026;
            // docs/FEHLERGEDAECHTNIS.md, No. 963)
            if (!intact && !compress && !identityCrypt && !original.ContainsKey(PdfName.F) && chain is [("FlateDecode", _)] && Flate.Repaired(raw) is { } repaired)
            {
                WriteStreamData(number, dict, repaired, compressed: false, encrypt, source, cipher);
                return;
            }
            WriteStreamData(number, dict, compress ? Compress(raw) : raw, compress, encrypt, source, cipher);
        }

        /// <summary>
        /// A stream packed with Brotli, unpacked: the data, and the image format left at the end of the chain (JPEG and the like stay as they
        /// are); null if the chain has no Brotli or cannot be read (then it stays as stored — nothing is lost).
        /// </summary>
        private static (byte[] Data, PdfObject? Filter, PdfObject? Parms)? WithoutBrotli(PdfDictionary original, ReadOnlySpan<byte> raw)
        {
            if (!StreamFilters.Chain(original).Any(f => f.Name == "BrotliDecode")) return null;
            try
            {
                var decoded = StreamFilters.Decode(raw.ToArray(), original);
                if (decoded.Truncated) return null;
                return (decoded.Data, decoded.ImageFilter, decoded.ImageFilter is null ? null : decoded.ImageParms);
            }
            catch (Exception e) when (e is PdfException or InvalidDataException)
            {
                return null;
            }
        }

        /// <summary>Whether a stream of a source is an embedded file (/Type /EmbeddedFile, or named by an /EF of a file specification).</summary>
        private bool IsEmbeddedFileStream(PdfStream stream, Source source) =>
            stream.Dictionary.IsType(PdfName.Get("EmbeddedFile")) || EmbeddedFiles(source).Contains(stream) || RelatedFiles(source).Contains(stream);

        private static void RemoveFirstFilter(PdfDictionary dict)
        {
            foreach (var key in new[] { PdfName.Filter, PdfName.DecodeParms })
            {
                switch (dict.Get(key))
                {
                    case PdfArray { Count: > 1 } array:
                        dict.Set(key, new PdfArray(array.Skip(1)));
                        break;
                    default:
                        dict.Remove(key);
                        break;
                }
            }
        }

        private void WriteStreamData(int number, PdfDictionary dict, ReadOnlySpan<byte> data, bool compressed, bool encrypt, Source? source,
            PdfCipher? cipher = null)
        {
            if (_pdfA is not null && !dict.IsType(PdfName.XRef) && !dict.IsType(PdfName.Get("ObjStm")) && !dict.IsType(PdfName.Metadata))
                dict = (PdfDictionary)PdfAFix(dict, source, number);
            FullFilterNames(dict);
            if (compressed)
            {
                // Only streams without filters are packed here, so /Filter is simply set to FlateDecode
                dict.Set(PdfName.Filter, FlateDecode);
                dict.Remove(PdfName.DecodeParms);
            }
            // what needs a later version than 1.4 (No. 553): JPEG 2000 and crypt filters of their own (1.5), OpenType fonts (1.6)
            if (!Pdf20 && _pdfA is null && CompareVersions(_version, "1.6") < 0)
            {
                foreach (var (filter, _) in StreamFilters.Chain(dict))
                    if (filter == "JPXDecode") Needs("1.5", Tr.T("JPEG 2000"));
                    else if (filter == "Crypt") Needs("1.5", Tr.T("eigene Kryptfilter"));
                if (dict.GetName(PdfName.Subtype)?.Value == "OpenType") Needs("1.6", Tr.T("OpenType-Schriften"));
                // 16 bits per value, in the image and in the predictor (ISO 32000-2, 8.9.5.1 and 7.4.4.3, Table 8: PDF 1.5;
                // docs/FEHLERGEDAECHTNIS.md, No. 943)
                if (dict.GetInteger(Bpc) == 16 || StreamFilters.Chain(dict).Any(f => f.Parms?.GetInteger(Bpc) == 16)) Needs("1.5", Tr.T("16-Bit-Bilder"));
            }
            // a stream whose contents are an external file (/F) is not encrypted — they are not part of the PDF file (ISO 32000-2, 7.6.2;
            // docs/FEHLERGEDAECHTNIS.md, No. 751)
            if (dict.GetRaw(PdfName.Get("F")) is not null) encrypt = false;
            if (encrypt && _security is { } security)
                data = cipher is { } method ? security.EncryptStream(new PdfObjectId(number, 0), data.ToArray(), method)
                    : security.EncryptStream(new PdfObjectId(number, 0), data.ToArray());
            dict.Set(PdfName.Length, PdfInteger.Of(data.Length));

            _xref[number] = (1, _position, 0);
            _scratch.Clear();
            _scratch.Int(number);
            _scratch.Ascii(" 0 obj\n");
            PdfSerializer.Write(_scratch, dict, source?.Mapper ?? NoReferences, StringEncryption(number, !dict.IsType(PdfName.XRef)));
            _scratch.Ascii("\nstream\n");
            Emit(_scratch.Written);
            Emit(data);
            _scratch.Clear();
            _scratch.Ascii("\nendstream\nendobj\n");
            Emit(_scratch.Written);
        }

        /// <summary>
        /// The abbreviated filter names (/AHx, /A85, /LZW, /Fl, /RL, /CCF, /DCT) and /DP belong to inline images only (ISO 32000-2, 8.9.7,
        /// Table 92); readers are lenient with them in a stream, the copy writes the full names and /DecodeParms — the data stays as it is
        /// (better than the standard asks of a reader; docs/FEHLERGEDAECHTNIS.md, No. 879). A /Filter given as a reference stays as it is.
        /// </summary>
        private static void FullFilterNames(PdfDictionary dict)
        {
            switch (dict.GetRaw(PdfName.Filter))
            {
                case PdfName name when StreamFilters.FullName(name.Value) is { } full:
                    dict.Set(PdfName.Filter, PdfName.Get(full));
                    break;
                case PdfArray array when array.Any(item => item is PdfName n && StreamFilters.FullName(n.Value) is not null):
                    dict.Set(PdfName.Filter, new PdfArray(array.Select(item => item is PdfName n && StreamFilters.FullName(n.Value) is { } full ? PdfName.Get(full) : item)));
                    break;
            }
            // /DP: as read, it counts only without /DecodeParms (StreamFilters.Chain)
            if (dict.GetRaw(PdfName.DP) is { } parms)
            {
                dict.Remove(PdfName.DP);
                if (!dict.ContainsKey(PdfName.DecodeParms)) dict.Set(PdfName.DecodeParms, parms);
            }
        }

        /// <summary>Packs into a reused buffer; the result is valid until the next call.</summary>
        private ReadOnlySpan<byte> Compress(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second = default)
        {
            _zip.SetLength(0);
            // "Flate – strong": object streams and previously unpacked streams as well (both parts one after the other)
            if (_options.Compression is { StrongFlate: true })
            {
                var all = new byte[first.Length + second.Length];
                first.CopyTo(all);
                second.CopyTo(all.AsSpan(first.Length));
                _zip.Write(Pack(all, brotli: false));
                return _zip.GetBuffer().AsSpan(0, (int)_zip.Length);
            }
            using (var z = new ZLibStream(_zip, PackLevel, leaveOpen: true))
            {
                z.Write(first);
                z.Write(second);
            }
            return _zip.GetBuffer().AsSpan(0, (int)_zip.Length);
        }

        // ------------------------------------------------------------ Cross-references

        // First part of /ID: that of the source, otherwise new; with a kept encryption it must stay (the key depends on it)
        private byte[] _firstId = [];

        private PdfArray FileId() =>
            new([new PdfString(_firstId, isHex: true), new PdfString(RandomNumberGenerator.GetBytes(16), isHex: true)]);

        private void FillTrailer(PdfDictionary trailer)
        {
            trailer.Set(PdfName.Size, PdfInteger.Of(_xref.Count));
            trailer.Set(PdfName.Root, new OutputReference(_catalogNumber));
            if (_infoNumber > 0) trailer.Set(PdfName.Info, new OutputReference(_infoNumber));
            if (_encryptNumber > 0) trailer.Set(PdfName.Encrypt, new OutputReference(_encryptNumber));
            else if (_encryptInline is not null) trailer.Set(PdfName.Encrypt, _encryptInline);   // its strings stay plain: the stream is not encrypted
            trailer.Set(PdfName.ID, FileId());
            if (_writeMac) trailer.Set(PdfName.Get("AuthCode"), PdfMac.StandalonePlaceholder());
        }

        private void WriteXRef()
        {
            if (_writeMac) _tail = new MemoryStream();
            // Numbers that were not assigned (there should be none) are entered as free
            if (!_objectStreams)
            {
                var start = _position;
                _scratch.Clear();
                _scratch.Ascii("xref\n0 ");
                _scratch.Int(_xref.Count);
                _scratch.Add((byte)'\n');
                for (var i = 0; i < _xref.Count; i++)
                {
                    var (type, offset, _) = _xref[i];
                    if (type == 1)
                    {
                        _scratch.Padded(offset, 10);
                        _scratch.Ascii(" 00000 n \n");
                    }
                    else
                    {
                        // a number not given out (there should be none): free and never to be used again, linked back to 0 (ISO 32000-2, 7.5.4)
                        _scratch.Ascii("0000000000 65535 f \n");
                    }
                    if (_scratch.Length > 60_000)
                    {
                        Emit(_scratch.Written);
                        _scratch.Clear();
                    }
                }
                var trailer = new PdfDictionary();
                FillTrailer(trailer);
                _scratch.Ascii("trailer\n");
                PdfSerializer.Write(_scratch, trailer, NoReferences, null);
                _scratch.Ascii("\nstartxref\n");
                _scratch.Int(start);
                _scratch.Ascii("\n%%EOF\n");
                Emit(_scratch.Written);
                return;
            }

            var number = NewNumber();
            var position = _position;
            _xref[number] = (1, position, 0);
            long max = 0;
            foreach (var (_, f2, _) in _xref) max = Math.Max(max, f2);
            var w2 = 1;
            while (w2 < 8 && max >= 1L << (8 * w2)) w2++;
            var entry = 1 + w2 + 2;
            var data = new byte[_xref.Count * entry];
            for (var i = 0; i < _xref.Count; i++)
            {
                var (type, f2, f3) = _xref[i];
                if (type == 0) (f2, f3) = (0, 65535);   // as in the table above
                var at = i * entry;
                data[at] = type;
                for (var k = 0; k < w2; k++) data[at + 1 + k] = (byte)(f2 >> (8 * (w2 - 1 - k)));
                data[at + 1 + w2] = (byte)(f3 >> 8);
                data[at + 2 + w2] = (byte)f3;
            }
            var dict = new PdfDictionary();
            dict.Set(PdfName.Type, PdfName.XRef);
            FillTrailer(dict);
            dict.Set(PdfName.W, new PdfArray([PdfInteger.Of(1), PdfInteger.Of(w2), PdfInteger.Of(2)]));
            // The cross-reference stream is never encrypted
            WriteStreamData(number, dict, Compress(data), compressed: true, encrypt: false, null);
            _scratch.Clear();
            _scratch.Ascii("startxref\n");
            _scratch.Int(position);
            _scratch.Ascii("\n%%EOF\n");
            Emit(_scratch.Written);
        }
    }
}
