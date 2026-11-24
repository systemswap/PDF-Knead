using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Platform;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>
/// Signs a document digitally by appending one incremental update (ISO 32000-2:2020, section 12.8; PAdES baseline B-B): a
/// signature dictionary, the signature field with the signature as its visible appearance, the page and the form. The bytes
/// before the update stay unchanged, so earlier signatures stay valid.
/// </summary>
/// <remarks>
/// The appearance is drawn by the ordinary writer onto a page the size of the signature, so it looks exactly as the signature
/// would look fixed in the page (handwriting, ballpoint texture, origin line), fonts subset as usual; the content of that page
/// becomes the appearance stream. The whole file is held in memory while signing.
/// </remarks>
public static partial class PdfSigner
{
    /// <summary>Reserved size of the CMS block; a block from a certificate with a long chain gets a second attempt with more room.</summary>
    private const int ReservedBytes = 12 * 1024;

    /// <summary>
    /// The first room for a block at most: a string of PDF/A-2 to -4 holds 32 767 bytes (ISO 19005-2, 6.1.13); a block with a time stamp got
    /// 36 KB and broke PDF/A (docs/FEHLERGEDAECHTNIS.md, No. 715). Only a larger block takes more on the second attempt.
    /// </summary>
    internal const int MaxFirstReserve = 32767;

    private const string RangePlaceholder = "[0 9999999999 9999999999 9999999999]";

    private static readonly PdfName Annots = PdfName.Get("Annots"), AcroForm = PdfName.Get("AcroForm"), Fields = PdfName.Get("Fields"),
        SigFlags = PdfName.Get("SigFlags"), FieldType = PdfName.Get("FT"), Title = PdfName.Get("T"), Value = PdfName.Get("V"),
        Appearance = PdfName.Get("AP"), Rect = PdfName.Get("Rect"), Flags = PdfName.Get("F"), PageRef = PdfName.Get("P"),
        ByteRange = PdfName.Get("ByteRange");

    /// <summary>Signs one signature of a document.</summary>
    /// <param name="doc">The document to append to; it must qualify for an incremental update (<see cref="IncrementalUpdate.CanAppend"/>).</param>
    /// <param name="pageIndex">Page of the signature (zero-based).</param>
    /// <param name="signature">The signature element (its <see cref="PageElement.Digital"/> names the field, if any).</param>
    /// <param name="certificate">The signing certificate with its private key.</param>
    /// <param name="when">Signing time (written as <c>/M</c>).</param>
    /// <returns>The signed file.</returns>
    /// <exception cref="NotAppendableException">The document does not qualify for an incremental update, or it had to repair its
    /// cross-references while the signature was prepared.</exception>
    /// <exception cref="NotSupportedException">The page is not an object of its own.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The key refused to sign.</exception>
    /// <param name="timestamps">A time-stamping authority for a signature time stamp (PAdES B-T); null for none.</param>
    /// <exception cref="TimestampException">A time stamp was asked for and could not be obtained (nothing is signed then).</exception>
    public static byte[] Sign(PdfDocument doc, int pageIndex, PageElement signature, SigningCertificate certificate, DateTimeOffset when,
        ITimestampAuthority? timestamps = null) => Signed(doc, SignedUpdate(doc, pageIndex, signature, certificate, when, timestamps));

    /// <summary>
    /// Signs like <see cref="Sign"/> but without checking the certificate first — only for the tests, which build files as another program
    /// might write them (an expired or unsuitable certificate) to check how such signatures are judged (No. 526).
    /// </summary>
    /// <inheritdoc cref="Sign" path="/param"/>
    /// <param name="writeMac">Whether a MAC is written where the file asks for one (ISO/TS 32004); null: as switched (<see cref="Security.PdfMac.WritingOn"/>).</param>
    internal static byte[] SignUnchecked(PdfDocument doc, int pageIndex, PageElement signature, SigningCertificate certificate, DateTimeOffset when,
        ITimestampAuthority? timestamps = null, bool? writeMac = null) =>
        Signed(doc, SignedUpdate(doc, pageIndex, signature, certificate, when, timestamps, checkCertificate: false, writeMac: writeMac));

    private static byte[] Signed(PdfDocument doc, byte[] update)
    {
        var file = new byte[doc.FileSize + update.Length];
        doc.FileBytes(0, doc.FileSize).CopyTo(file);
        update.CopyTo(file.AsSpan((int)doc.FileSize));
        return file;
    }

    /// <summary>
    /// Signs one signature and returns only the update to append to the document's bytes: for saving in several appending steps, which
    /// keeps the file in one buffer instead of copying it per signature.
    /// </summary>
    /// <inheritdoc cref="Sign" path="/param"/>
    /// <returns>The bytes that follow the document's bytes in the signed file.</returns>
    /// <inheritdoc cref="Sign" path="/exception"/>
    /// <param name="made">Receives the CMS block of the signature (for the validation data that follows it); null if not needed.</param>
    internal static byte[] SignedUpdate(PdfDocument doc, int pageIndex, PageElement signature, SigningCertificate certificate, DateTimeOffset when,
        ITimestampAuthority? timestamps = null, List<byte[]>? made = null, bool checkCertificate = true,
        IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2>? extraChain = null, bool? writeMac = null)
    {
        var mac = writeMac ?? Security.PdfMac.WritingOn;
        // an expired, not yet valid or unsuitable certificate signs nothing — every checker would call the signature invalid; valid at the
        // moment the key signs, by the clock (the /M a caller passes is only claimed; No. 526)
        if (checkCertificate && Platform.SigningCertificates.Unusable(certificate.Certificate, DateTimeOffset.UtcNow) is { } unusable)
            throw new Platform.SigningCertificateException(Tr.T("Mit diesem Zertifikat lässt sich nicht unterschreiben: {0}", unusable));
        signature = WithCertificateHolder(signature, certificate.Certificate);
        // An empty field of the file is filled where the signature lies within it; moved out of it, the signature gets a field of its own
        var target = signature.Digital?.FieldName is { Length: > 0 } name ? EmptySignatureField(doc, name) : null;
        if (target is { } found && !Within(signature.Rect, found.Rect)) target = null;
        // an encrypted file opened without full access (ISO 32000-2, Table 22; Table 24 for recipients): filling an existing signature field
        // needs bit 9 (from revision 3; revision 2 does not know it) or bit 6; a signature field of its own is created — bits 4 and 6 together.
        // Before, bit 6 or 9 sufficed in every case (second M4 probe, D; docs/FEHLERGEDAECHTNIS.md, No. 543, 1111, 1119)
        if (doc.Security is { IsOwner: false } rights)
        {
            var p = rights.Permissions;
            var fill = p.HasFlag(Security.PdfPermissions.Annotate) || rights.Revision >= 3 && p.HasFlag(Security.PdfPermissions.FillForms);
            var create = p.HasFlag(Security.PdfPermissions.Annotate) && p.HasFlag(Security.PdfPermissions.Modify);
            if (target is null ? !create : !fill)
                throw new PdfException(Tr.T("Die Rechte dieser Datei erlauben kein Unterschreiben."));
        }
        // the seed values of the prepared field (Table 237): a requirement PDF Knead cannot meet refuses signing into it — before anything is
        // written (docs/ABLAUFPLAN.md, 2.3, phase 1: refuse, adapt nothing)
        if (target is { } prepared && SeedValues.Of(prepared.Field) is { } seed
            && seed.Check(SigningPlan.Of(timestamps is not null, signature.Digital?.Certify, DigitalSignatures.Find(doc).Count == 0, certificate.Certificate))
                is { Refusals.Count: > 0 } verdict)
            throw new SeedValueException(SeedValues.Refused(verdict.Refusals));
        var box = target?.Rect ?? signature.Rect;
        var appearance = AppearancePage(signature, box);
        try
        {
            // with a time stamp, room for its token from the start: another attempt would need a new one from the service; a key that signs
            // only once (a card, later a remote service: IRemoteKey) gets its room sized beforehand and no second attempt (docs/ABLAUFPLAN.md,
            // 9.4.2; docs/FEHLERGEDAECHTNIS.md, No. 718)
            IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2> links =
                extraChain is { Count: > 0 } ? [.. certificate.Chain, .. extraChain] : certificate.Chain;
            var once = certificate.Key is Platform.IRemoteKey;
            // a MAC for the signed revision (ISO/TS 32004, 6.5.2) has its room as well
            var first = once ? CmsSignature.SizeFor(certificate.Certificate, links) + (timestamps is null ? 0 : SignatureTimestamps.ReservedBytes) + Security.PdfMac.AttachedLength
                : Math.Min(ReservedBytes + (timestamps is null ? 0 : SignatureTimestamps.ReservedBytes), MaxFirstReserve);
            for (var reserve = first; ; reserve *= 4)
            {
                var (bytes, gap, attach) = Prepare(doc, pageIndex, signature, target, box, appearance, certificate, when, reserve, mac);
                // Signed: the document's bytes and the update, without the placeholder of the CMS block — the digest is streamed over
                // them; only a key that is neither RSA nor ECDSA needs them as one array
                // the issuers fetched beforehand (a card that carries only its own certificate) go into the block as well (No. 533; links)
                var cms = CmsSignature.CreateFromDigest(Digest(doc, bytes, gap, CmsSignature.HashFor(certificate.Certificate)), certificate.Certificate,
                        links, certificate.Key)
                    ?? CmsSignature.Create(Content(doc, bytes, gap), certificate.Certificate, links, certificate.Key);
                if (timestamps is not null) cms = SignatureTimestamps.AddTo(cms, timestamps);
                // the MAC of the signed revision, attached to the signature last (6.5.2, 6.6.3): over the same bytes the signature covers
                if (attach) cms = Security.PdfMac.AttachTo(cms, Security.PdfMac.KekOf(doc), Digest(doc, bytes, gap));
                if (2 * cms.Length > gap.End - gap.Start - 2)
                {
                    if (once)
                        throw new System.Security.Cryptography.CryptographicException(Tr.T("Die Signatur ist größer als der vorab bestimmte Platz; es wurde nichts gespeichert und nicht ein zweites Mal unterschrieben."));
                    if (reserve > 1 << 20) throw new System.Security.Cryptography.CryptographicException(Tr.T("Die Signatur ist zu groß."));
                    continue;
                }
                var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(cms));
                hex.CopyTo(bytes.AsSpan(gap.Start + 1));
                made?.Add(cms);
                return bytes;
            }
        }
        finally
        {
            appearance.Dispose();
        }
    }

    /// <summary>
    /// Writes the update with a placeholder for the CMS block and fills in /ByteRange; returns the update and the placeholder (positions
    /// within the update).
    /// </summary>
    /// <summary>/Prop_Build of a signature: << /App << /Name /PDF-Knead /REx (version) >> >> (ISO 32000-2, 12.8.1; the build properties as
    /// the PDF Signature Build Dictionary Specification names them).</summary>
    internal static PdfDictionary PropBuild()
    {
        var app = new PdfDictionary();
        app.Set(PdfName.Name, PdfName.Get("PDF-Knead"));
        app.Set(PdfName.Get("REx"), new PdfString(PdfTextEncoding.Encode(Platform.AppVersion.Raw)));
        var build = new PdfDictionary();
        build.Set(PdfName.Get("App"), app);
        return build;
    }

    private static (byte[] Bytes, (int Start, int End) Gap, bool Mac) Prepare(PdfDocument doc, int pageIndex, PageElement signature, EmptyField? target,
        PdfRectangle box, PdfDocument appearance, SigningCertificate certificate, DateTimeOffset when, int reserve, bool writeMac)
    {
        if (pageIndex < 0 || pageIndex >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        var page = doc.Pages[pageIndex];
        if (page.Id is not { } pageId) throw new NotSupportedException(Tr.T("Die Seite ist kein eigenes Objekt."));
        var update = new IncrementalUpdate(doc, writeMac);

        var form = update.Add(AppearanceForm(appearance, update, box));
        var sig = new PdfDictionary();
        sig.Set(PdfName.Type, PdfName.Get("Sig"));
        sig.Set(PdfName.Filter, PdfName.Get("Adobe.PPKLite"));
        sig.Set(PdfName.Get("SubFilter"), PdfName.Get("ETSI.CAdES.detached"));
        sig.Set(ByteRange, new PdfArray { PdfInteger.Of(0), PdfInteger.Of(9_999_999_999), PdfInteger.Of(9_999_999_999), PdfInteger.Of(9_999_999_999) });
        sig.Set(PdfName.Contents, new PdfString(new byte[reserve], isHex: true));
        sig.Set(PdfName.Get("M"), new PdfString(Encoding.ASCII.GetBytes(Document.PdfDate.Format(when, PdfWriter.CompareVersions(doc.Version, "2.0") >= 0))));
        // /Name only where the name cannot be read from the signature itself: a certificate without a readable name, then the name the
        // signer gave (ISO 32000-2, Table 255; EN 319 142-1 V1.3.0, 5.3; docs/FEHLERGEDAECHTNIS.md, No. 538)
        if (certificate.Name.Trim().Length == 0 && signature.Digital?.Signer?.Trim() is { Length: > 0 } given)
            sig.Set(PdfName.Name, new PdfString(PdfTextEncoding.Encode(given)));
        // PDF 2.0: the program that signed, by name and version — recommended by the PAdES draft for ISO 32000-2 signatures (EN 319 142-1
        // V1.3.0, 5.3; ISO 32000-2, Table 255), direct, nothing about the computer (the program's name is allowed, design decision;
        // docs/FEHLERGEDAECHTNIS.md, No. 532)
        if (PdfWriter.CompareVersions(doc.Version, "2.0") >= 0) sig.Set(PdfName.Get("Prop_Build"), PropBuild());
        var rootRef = (PdfReference)doc.Trailer.GetRaw(PdfName.Root)!;
        // Certifying (DocMDP) only as the first signature of the document; locking fields (FieldMDP) with any (ISO 32000-2:2020, 12.8.2)
        var certify = signature.Digital?.Certify is { } level && DigitalSignatures.Find(doc).Count == 0 ? level : (CertificationLevel?)null;
        var lockFields = signature.Digital?.LockFields == true;
        var references = new PdfArray();
        if (certify is { } p)
            references.Add(SignatureReference("DocMDP", ("P", PdfInteger.Of((int)p))));
        // The lock the form's author put into the prepared field (/Lock, Table 236): its /Action and /Fields go into the signature word for
        // word, also /All (12.8.2.4; the names are full field names, erratum EC3 #33); it takes precedence over „lock fields“, and the field
        // keeps it. Without one, „lock fields“ locks every field there is, as an /Include list (docs/FEHLERGEDAECHTNIS.md, No. 330)
        var authorLock = target?.Field.Get(LockKey) as PdfDictionary;
        var locked = authorLock is null && lockFields ? FieldsToLock(doc, target?.Name) : null;
        if (authorLock is not null)
        {
            var action = authorLock.GetName(PdfName.Get("Action")) is { Value: "All" or "Include" or "Exclude" } named ? named : PdfName.Get("All");
            var parameters = new List<(string, PdfObject)> { ("Action", action) };
            if (action.Value != "All" && authorLock.GetArray(PdfName.Get("Fields")) is { } authorFields)
            {
                var names = new PdfArray();
                for (var i = 0; i < Math.Min(authorFields.Count, 10_000); i++)
                    if (authorFields.Get(i) is PdfString name) names.Add(name);
                parameters.Add(("Fields", names));
            }
            references.Add(WithData(SignatureReference("FieldMDP", [.. parameters]), rootRef));
        }
        else if (locked is not null) references.Add(WithData(SignatureReference("FieldMDP", ("Action", PdfName.Get("Include")), ("Fields", locked)), rootRef));
        if (references.Count > 0) sig.Set(PdfName.Get("Reference"), references);
        var sigRef = update.Add(sig);
        if (update.WritesMac) update.MacSignature = sigRef;

        var appearanceDict = new PdfDictionary();
        appearanceDict.Set(PdfName.N, form);
        var catalog = IncrementalParts.Copy(doc.Catalog!);
        var acroFormRaw = doc.Catalog!.GetRaw(AcroForm);
        var acroForm = doc.Catalog.GetDictionary(AcroForm) is { } existing ? IncrementalParts.Copy(existing) : new PdfDictionary();
        var fieldsRaw = acroForm.GetRaw(Fields);
        var fields = acroForm.GetArray(Fields) is { } list ? new PdfArray(list) : new PdfArray();

        if (target is { } empty)
        {
            // An empty field of the file: the field receives the signature, its widget the appearance; both stay where they are
            var field = IncrementalParts.Copy(empty.Field);
            field.Set(Value, sigRef);
            // an own lock only where the author set none (never overwritten), as an indirect object (Table 235)
            if (locked is not null) field.Set(LockKey, update.Add(FieldsLock(locked)));
            if (empty.WidgetId == empty.FieldId)
            {
                field.Set(Appearance, appearanceDict);
            }
            else
            {
                var widget = IncrementalParts.Copy(empty.Widget);
                widget.Set(Appearance, appearanceDict);
                update.Replace(empty.WidgetId, widget);
            }
            update.Replace(empty.FieldId, field);
        }
        else
        {
            var field = new PdfDictionary();
            field.Set(PdfName.Type, PdfName.Get("Annot"));
            field.Set(PdfName.Subtype, PdfName.Get("Widget"));
            field.Set(FieldType, PdfName.Get("Sig"));
            field.Set(Title, new PdfString(PdfTextEncoding.Encode(UniqueName(doc, signature.Digital?.FieldName))));
            field.Set(Value, sigRef);
            field.Set(Rect, new PdfArray { new PdfReal(signature.Rect.Left), new PdfReal(signature.Rect.Bottom), new PdfReal(signature.Rect.Right), new PdfReal(signature.Rect.Top) });
            field.Set(Flags, PdfInteger.Of(4 | 128));   // print, locked
            field.Set(PageRef, new PdfReference(pageId));
            field.Set(Appearance, appearanceDict);
            if (locked is not null) field.Set(LockKey, update.Add(FieldsLock(locked)));
            var fieldRef = update.Add(field);
            fields.Add(fieldRef);
            // a tagged file: the visible signature is real content — a Form element, a neutral name without the signer (No. 635); also after
            // earlier signatures: the change check counts the structure added for a new field as filling in and signing (No. 637), and so does
            // the reference checker (an extension of the document)
            var tagged = IncrementalStructure.TagNewWidget(doc, update, page, field, fieldRef);
            if (tagged)
            {
                var name = new PdfString(PdfTextEncoding.Encode(Accessibility.FixedTexts.DigitalSignature(doc.Catalog?.GetString(PdfName.Get("Lang"))?.ToText())));
                field.Set(PdfName.Get("TU"), name);
                field.Set(PdfName.Contents, name);
            }
            IncrementalParts.AddAnnotations(update, page, pageId, [fieldRef], tabsByStructure: tagged);
        }

        acroForm.Set(SigFlags, PdfInteger.Of(3));   // signatures exist, append only
        if (fieldsRaw is PdfReference fieldsRef) update.Replace(fieldsRef.Id, fields);
        else acroForm.Set(Fields, fields);
        var catalogChanged = false;
        if (acroFormRaw is PdfReference acroFormRef)
        {
            update.Replace(acroFormRef.Id, acroForm);
        }
        else
        {
            catalog.Set(AcroForm, acroForm);
            catalogChanged = true;
        }
        if (certify is not null)
        {
            // The catalog names the certification signature (12.8.4)
            var perms = catalog.GetDictionary(PdfName.Get("Perms")) is { } existingPerms ? IncrementalParts.Copy(existingPerms) : new PdfDictionary();
            perms.Set(PdfName.Get("DocMDP"), sigRef);
            catalog.Set(PdfName.Get("Perms"), perms);
            catalogChanged = true;
        }
        // ETSI.CAdES.detached before PDF 2.0: the PAdES extension, declared (PadesExtension)
        if (PadesExtension.Declare(catalog, doc, PadesExtension.Signatures)) catalogChanged = true;
        // ECDSA in PDF 2.0: ISO/TS 32002, declared (No. 536); not before 2.0 (ISO 32000-2, 7.12.4)
        if (CmsSignature.IsEcdsa(certificate.Certificate) && PadesExtension.DeclareIso32002(catalog, doc)) catalogChanged = true;
        if (catalogChanged) update.Replace(rootRef.Id, catalog);

        using var output = new MemoryStream();
        update.WriteUpdateTo(output);
        var bytes = output.ToArray();
        var gap = Placeholder(bytes, update.Offsets[((PdfReference)sigRef).Id.Number] - doc.FileSize);
        WriteRange(bytes, gap, doc.FileSize);
        return (bytes, gap, update.WritesMac);
    }

    private static readonly PdfName LockKey = PdfName.Get("Lock");

    /// <summary>The digest of the signed bytes: the document's bytes, then the update without the placeholder (SHA-256 unless named).</summary>
    internal static byte[] Digest(PdfDocument doc, byte[] update, (int Start, int End) gap, HashAlgorithmName? algorithm = null)
    {
        using var hash = IncrementalHash.CreateHash(algorithm ?? HashAlgorithmName.SHA256);
        for (long at = 0; at < doc.FileSize; at += 1 << 20) hash.AppendData(doc.FileBytes(at, 1 << 20));
        hash.AppendData(update.AsSpan(0, gap.Start));
        hash.AppendData(update.AsSpan(gap.End));
        return hash.GetHashAndReset();
    }

    /// <summary>The signed bytes as one array (only for keys <see cref="CmsSignature.CreateFromDigest"/> does not handle).</summary>
    private static byte[] Content(PdfDocument doc, byte[] update, (int Start, int End) gap)
    {
        var size = (int)doc.FileSize;
        var content = new byte[size + gap.Start + (update.Length - gap.End)];
        doc.FileBytes(0, size).CopyTo(content);
        update.AsSpan(0, gap.Start).CopyTo(content.AsSpan(size));
        update.AsSpan(gap.End).CopyTo(content.AsSpan(size + gap.Start));
        return content;
    }

    /// <summary>A signature reference dictionary (Table 256) with its transform parameters (Table 257, 258).</summary>
    private static PdfDictionary SignatureReference(string method, params (string Key, PdfObject Value)[] parameters)
    {
        var transform = new PdfDictionary();
        transform.Set(PdfName.Type, PdfName.Get("TransformParams"));
        foreach (var (key, value) in parameters) transform.Set(PdfName.Get(key), value);
        transform.Set(PdfName.Get("V"), PdfName.Get("1.2"));
        var reference = new PdfDictionary();
        reference.Set(PdfName.Type, PdfName.Get("SigRef"));
        reference.Set(PdfName.Get("TransformMethod"), PdfName.Get(method));
        reference.Set(PdfName.Get("TransformParams"), transform);
        return reference;
    }

    /// <summary>FieldMDP references the document's catalog as the object the lock is analysed on (/Data).</summary>
    private static PdfDictionary WithData(PdfDictionary reference, PdfReference root)
    {
        reference.Set(PdfName.Get("Data"), root);
        return reference;
    }

    /// <summary>
    /// The full names of all fields of the form at signing (every node of the field tree, except the signature's own field): „lock all
    /// fields“ is written as /Include with this list rather than /All. Both lock every field there is; /All would also cover fields added
    /// later — the field of a document time stamp or of a further signature (ISO 32000-2:2020, 12.8.2.4 and 12.8.4.3) —, which validators
    /// following ETSI EN 319 102-1 then report as a change to a locked field (docs/FEHLERGEDAECHTNIS.md, „Feldsperre und
    /// Dokument-Zeitstempel“).
    /// </summary>
    private static PdfArray FieldsToLock(PdfDocument doc, string? own)
    {
        var names = new PdfArray();
        if (doc.Catalog?.GetDictionary(AcroForm)?.GetArray(Fields) is not { } fields) return names;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, _, name) in IncrementalParts.Walk(doc, fields))
            if (name.Length > 0 && name != own && seen.Add(name)) names.Add(new PdfString(PdfTextEncoding.Encode(name)));
        return names;
    }

    /// <summary>The lock of a signature field (Table 236) for the same fields as the signature's FieldMDP transform.</summary>
    private static PdfDictionary FieldsLock(PdfArray names)
    {
        var lockDict = new PdfDictionary();
        lockDict.Set(PdfName.Type, PdfName.Get("SigFieldLock"));
        lockDict.Set(PdfName.Get("Action"), PdfName.Get("Include"));
        lockDict.Set(PdfName.Get("Fields"), new PdfArray(names));
        return lockDict;
    }

    /// <summary>An unsigned signature field of the file and its widget (the same object, or its only widget among the kids).</summary>
    private sealed record EmptyField(PdfObjectId FieldId, PdfDictionary Field, PdfObjectId WidgetId, PdfDictionary Widget, PdfRectangle Rect, string Name);

    /// <summary>An unsigned signature field of the file with this full name, as an object of its own; its type may be inherited.</summary>
    private static EmptyField? EmptySignatureField(PdfDocument doc, string name)
    {
        if (doc.Catalog?.GetDictionary(AcroForm)?.GetArray(Fields) is not { } fields) return null;
        foreach (var (id, field, full) in IncrementalParts.Walk(doc, fields))
        {
            if (full != name || !field.ContainsKey(Title) || field.Get(Value) is PdfDictionary) continue;
            // the field type inherited in any depth (ISO 32000-2:2020, 12.7.4.1): a prepared field two levels below /FT /Sig was not found
            if (DigitalSignatures.InheritedFieldType(field)?.Value != "Sig") continue;
            if (PdfRectangle.From(field.Get(Rect)) is { IsEmpty: false } own) return new EmptyField(id, field, id, field, own, full);
            // field and widget apart: its widget among the kids
            if (field.GetArray(PdfName.Kids) is { Count: 1 } kids && kids[0] is PdfReference kidRef && kids.Get(0) is PdfDictionary kid
                && PdfRectangle.From(kid.Get(Rect)) is { IsEmpty: false } kidRect)
                return new EmptyField(id, field, kidRef.Id, kid, kidRect, full);
        }
        return null;
    }

    /// <summary>Whether a rectangle lies within another (one point of tolerance for rounding).</summary>
    private static bool Within(PdfRectangle inner, PdfRectangle outer) =>
        Math.Min(inner.Left, inner.Right) >= Math.Min(outer.Left, outer.Right) - 1 && Math.Max(inner.Left, inner.Right) <= Math.Max(outer.Left, outer.Right) + 1
        && Math.Min(inner.Bottom, inner.Top) >= Math.Min(outer.Bottom, outer.Top) - 1 && Math.Max(inner.Bottom, inner.Top) <= Math.Max(outer.Bottom, outer.Top) + 1;

    /// <summary>
    /// A field name not yet used: the wanted one, otherwise the stem with a number — „Unterschrift1“, „Unterschrift2“ … by default, „Dokument-Zeitstempel 2“ …
    /// for a stem given with a trailing space.
    /// </summary>
    internal static string UniqueName(PdfDocument doc, string? wanted, string? stem = null)
    {
        var used = doc.Catalog?.GetDictionary(AcroForm)?.GetArray(Fields) is { } fields
            ? IncrementalParts.Walk(doc, fields).Select(f => f.Name).ToHashSet(StringComparer.Ordinal)
            : [];
        if (wanted is { Length: > 0 } && !used.Contains(wanted)) return wanted;
        stem ??= Tr.T("Unterschrift");
        for (var n = stem.EndsWith(' ') ? 2 : 1; ; n++)
            if (!used.Contains(stem + n.ToString(CultureInfo.InvariantCulture))) return stem + n.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A page the size of <paramref name="box"/> (the signature, or the field it fills) with the signature on it at its place, written by
    /// the ordinary writer.
    /// </summary>
    /// <summary>
    /// The signature with the holder of the certificate in its origin line — „Zertifikatsinhaber: …“ — where the name the signer gave is
    /// another than the certificate's (a visible name nobody vouches for); unchanged where they are the same or the signer gave none.
    /// </summary>
    internal static PageElement WithCertificateHolder(PageElement signature, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
    {
        if (signature.Digital is not { Signer: { Length: > 0 } signer } digital) return signature;
        var holder = certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false).Trim();
        if (holder.Length == 0 || SameName(signer, holder)) return signature;
        var line = Tr.T("Zertifikatsinhaber: {0}", holder);
        return signature with { Digital = digital with { Origin = digital.Origin is { Length: > 0 } origin ? origin + " · " + line : line } };
    }

    /// <summary>Two names alike in their words, whatever their order, case and commas („Mustermann, Max“ and „Max Mustermann“).</summary>
    internal static bool SameName(string a, string b)
    {
        static string[] Words(string name) =>
            [.. name.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).Order(StringComparer.Ordinal)];
        return Words(a).SequenceEqual(Words(b));
    }

    private static PdfDocument AppearancePage(PageElement signature, PdfRectangle box)
    {
        var (w, h) = (Math.Abs(box.Width), Math.Abs(box.Height));
        var (left, bottom) = (Math.Min(box.Left, box.Right), Math.Min(box.Bottom, box.Top));
        var blank = BlankPage.Create(w, h);
        var placed = ElementAppearance.Moved(signature, new PdfRectangle(signature.Rect.Left - left, signature.Rect.Bottom - bottom,
            signature.Rect.Right - left, signature.Rect.Top - bottom));
        using var output = new MemoryStream();
        PdfWriter.Write(blank.Source, [blank.WithElements([placed])], output, new PdfSaveOptions { Version = "1.7", ObjectStreams = false });
        return PdfDocument.Open(output.ToArray());
    }

    /// <summary>The appearance stream: the content of the page as a form, its resources copied into the update.</summary>
    private static PdfStream AppearanceForm(PdfDocument appearance, IncrementalUpdate update, PdfRectangle rect)
    {
        var page = appearance.Pages[0];
        var data = new MemoryStream();
        foreach (var stream in Contents(page.Dictionary.Get(PdfName.Contents)))
        {
            data.Write(appearance.GetStreamData(stream));
            data.WriteByte((byte)'\n');
        }
        var copied = new Dictionary<PdfObjectId, PdfReference>();
        var form = new PdfDictionary();
        form.Set(PdfName.Type, PdfName.Get("XObject"));
        form.Set(PdfName.Subtype, PdfName.Get("Form"));
        form.Set(PdfName.Get("BBox"), new PdfArray { PdfInteger.Of(0), PdfInteger.Of(0), new PdfReal(rect.Width), new PdfReal(rect.Height) });
        if (page.Resources is { } resources) form.Set(PdfName.Resources, IncrementalParts.Graft(appearance, update, resources, copied));
        form.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        return new PdfStream(form, Deflate(data.ToArray()));
    }

    private static IEnumerable<PdfStream> Contents(PdfObject contents) => contents switch
    {
        PdfStream stream => [stream],
        PdfArray array => Enumerable.Range(0, array.Count).Select(array.Get).OfType<PdfStream>(),
        _ => [],
    };

    /// <summary>Finds the hexadecimal /Contents string of the signature dictionary that starts at <paramref name="from"/>.</summary>
    internal static (int Start, int End) Placeholder(byte[] bytes, long from)
    {
        var text = Encoding.Latin1.GetString(bytes, (int)from, Math.Min(bytes.Length - (int)from, 1 << 22));
        var at = text.IndexOf("/Contents<", StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException("signature placeholder not found");
        var start = (int)from + at + "/Contents".Length;
        var end = Array.IndexOf(bytes, (byte)'>', start) + 1;
        return (start, end);
    }

    /// <summary>
    /// Replaces the /ByteRange placeholder in the update by the real ranges, padded with spaces to the same length; the update follows
    /// <paramref name="start"/> bytes of the document.
    /// </summary>
    internal static void WriteRange(byte[] bytes, (int Start, int End) gap, long start)
    {
        var text = Encoding.Latin1.GetString(bytes, 0, gap.Start);
        var at = text.LastIndexOf(RangePlaceholder, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException("byte range placeholder not found");
        var range = FormattableString.Invariant($"[0 {start + gap.Start} {start + gap.End} {bytes.Length - gap.End}]");
        var padded = range[..^1].PadRight(RangePlaceholder.Length - 1) + "]";
        Encoding.ASCII.GetBytes(padded).CopyTo(bytes, at);
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(data);
        return output.ToArray();
    }
}
