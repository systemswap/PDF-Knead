using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Platform;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>
/// Saving digitally signed files and digital signatures still to be signed (elements with <see cref="PageElement.Digital"/>): first
/// the document without those signatures — appended to the original bytes if the file carries intact signatures and only allowed
/// changes were made (<see cref="AppendedChanges"/>), so the earlier signatures stay valid; otherwise written anew as usual —, then
/// one incremental update per signature (<see cref="PdfSigner"/>).
/// </summary>
public static class SignedSaving
{
    /// <summary>Whether any page has a signature still to be signed.</summary>
    /// <param name="pages">The pages.</param>
    /// <returns>true if at least one is pending.</returns>
    public static bool HasPending(IReadOnlyList<PageItem> pages) => pages.Any(p => p.Elements.Any(e => e.Digital is not null));

    /// <summary>
    /// Whether saving only appends to the file, so that its intact signatures stay valid: it has some, can take an incremental update,
    /// and the pages differ from the saved ones only by changes that can be appended (<see cref="AppendedChanges.CanAppend"/>) and by
    /// signatures still to be signed.
    /// </summary>
    /// <param name="main">The opened document.</param>
    /// <param name="saved">The pages as last saved (or opened).</param>
    /// <param name="pages">The pages to save.</param>
    /// <returns>true if appending is enough.</returns>
    /// <remarks>Options that change the whole file (compression, protection, flattening, as images) are the caller's to rule out.</remarks>
    public static bool AppendsOnly(PdfDocument main, IReadOnlyList<PageItem> saved, IReadOnlyList<PageItem> pages) =>
        AppendsOnly(main, saved, pages, out _);

    /// <inheritdoc cref="AppendsOnly(PdfDocument, IReadOnlyList{PageItem}, IReadOnlyList{PageItem})"/>
    /// <param name="reason">If a restriction of the signatures (certification, locked fields) prevents appending: why; otherwise null.</param>
    public static bool AppendsOnly(PdfDocument main, IReadOnlyList<PageItem> saved, IReadOnlyList<PageItem> pages, out string? reason)
    {
        reason = null;
        if (!IncrementalUpdate.CanAppend(main) || DigitalSignatures.CountIntact(main) == 0) return false;
        var permissions = SignaturePermissions.Of(main);
        if (!AppendedChanges.CanAppend(main, saved, pages, permissions, out reason)) return false;
        // a further signature: not in a document certified with „no changes“
        if (HasPending(pages) && !permissions.AllowsFormsAndSigning)
        {
            reason = Tr.T("Das Dokument ist zertifiziert: danach sind keine Änderungen erlaubt.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Writes the document into a file (safely, see <see cref="PdfFileSaver"/>): written anew or appended to the signed original, then
    /// each signature still to be signed as an update of its own.
    /// </summary>
    /// <param name="main">The main document.</param>
    /// <param name="pages">The pages to save, with the signatures still to be signed.</param>
    /// <param name="path">The target path; may be the open file.</param>
    /// <param name="options">The save options.</param>
    /// <param name="appendOnly">Append the changes to the original bytes (<see cref="AppendsOnly"/>) instead of writing anew.</param>
    /// <param name="certificate">The signing certificate; may be null only if nothing is to be signed.</param>
    /// <param name="when">Signing time.</param>
    /// <param name="cancel">Cancels before signing; the target stays unchanged.</param>
    /// <returns>The result; <see cref="PdfSaveResult.Length"/> is the length of the file.</returns>
    /// <exception cref="PdfException">The changes cannot be appended, or the written file cannot take an incremental update, for
    /// example because it is encrypted (signing encrypted documents is not supported yet); nothing is written.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The key refused to sign.</exception>
    /// <exception cref="TimestampException">A time stamp was asked for and could not be obtained.</exception>
    /// <param name="timestamps">A time-stamping authority for signature time stamps (PAdES B-T); null for none.</param>
    /// <param name="revocation">
    /// Where to ask for the revocation status of the new signatures' certificates (PAdES B-LT, <see cref="LongTermValidation"/>); null for
    /// none. What could not be obtained is listed in <see cref="PdfSaveResult.LongTermMissing"/>, the file is saved all the same.
    /// </param>
    /// <exception cref="CertificateRevokedException">A certificate of a new signature is revoked; nothing is written.</exception>
    /// <param name="archive">
    /// Append a document time stamp after the validation data (PAdES B-LTA, <see cref="PdfSigner.DocumentTimestampUpdate"/>) — only with
    /// <paramref name="timestamps"/>; with <paramref name="revocation"/> its own validation data follow.
    /// </param>
    /// <param name="certificateFor">The certificate of a signature that names its own (<see cref="DigitalSigning.With"/>); null or a null answer:
    /// <paramref name="certificate"/>. The caller owns them.</param>
    public static PdfSaveResult Save(PdfDocument main, IReadOnlyList<PageItem> pages, string path, PdfSaveOptions options, bool appendOnly,
        SigningCertificate? certificate, DateTimeOffset when, CancellationToken cancel = default, ITimestampAuthority? timestamps = null,
        IRevocationSource? revocation = null, bool archive = false, Func<DigitalSigning, SigningCertificate?>? certificateFor = null)
    {
        // the certifying signature first: only the first signature of a document can certify it
        var pending = pages.SelectMany((p, i) => p.Elements.Where(e => e.Digital is not null).Select(e => (Page: i, Element: e)))
            .OrderBy(p => p.Element.Digital!.Certify is null ? 1 : 0).ToList();
        // the certificate of each signature: its own (chosen for it alone) or the default
        SigningCertificate? For(PageElement element) => certificateFor?.Invoke(element.Digital!) ?? certificate;
        if (pending.Any(p => For(p.Element) is null)) throw new ArgumentNullException(nameof(certificate));
        // a key that signs once per signature learns beforehand how many there are (a remote service: one release for all; 9.4.2)
        foreach (var group in pending.GroupBy(p => For(p.Element)!))
            if (group.Key.Key is Platform.IRemoteKey remote) remote.Expect(group.Count());
        var without = pages.Select(p => p.Elements.Any(e => e.Digital is not null)
            ? p.WithElements([.. p.Elements.Where(e => e.Digital is null)]) : p).ToArray();
        // One buffer for all steps: each signature is appended behind the bytes before it, the file is not copied per step
        using var file = SaveBuffer.Create(main.FileSize, pending.Count * 64L * 1024);
        PdfSaveResult result;
        if (appendOnly)
        {
            AppendedChanges.ApplyTo(main, without, options, file, cancel);
            result = new PdfSaveResult(main.Version, file.Length, 0, false);
        }
        else
        {
            result = PdfWriter.Write(main, without, file, options, cancel);
        }
        var made = new List<byte[]>();
        // an encrypted file is opened again after each step with the key it was written with, not with a stored password (No. 543)
        var security = appendOnly ? main.Security : result.Security;
        // with the long-term validation (the network allowed): the issuers that did not come with the certificate, fetched before signing so
        // they stand in the CMS block (No. 533)
        var issuers = revocation is not null && pending.Count > 0
            ? pending.Select(p => For(p.Element)!).Distinct().SelectMany(c => RevocationInfo.MissingIssuers(c.Certificate, [.. c.Chain], revocation, cancel)).ToList()
            : [];
        try
        {
            foreach (var (page, element) in pending)
            {
                cancel.ThrowIfCancellationRequested();
                byte[] update;
                using (var doc = PdfDocument.OpenWritten(SaveBuffer.Source(file), security))
                {
                    if (!IncrementalUpdate.CanAppend(doc))
                        throw new PdfException(IncrementalUpdate.EncryptionRefusal(doc) ?? Tr.T("Die Datei lässt sich nicht digital signieren."));
                    update = PdfSigner.SignedUpdate(doc, page, element, For(element)!, when, timestamps, made, extraChain: issuers);
                }
                file.Write(update);
            }
        }
        finally
        {
            foreach (var issuer in issuers) issuer.Dispose();
        }
        if (revocation is not null && made.Count > 0)
        {
            // the validation data after the signatures: an update of its own that every signature allows
            // the certificates that came with the signing certificate as well: the CMS block leaves out the root
            var chains = made.SelectMany(LongTermValidation.ChainsOf).Select(c =>
            {
                var extra = new X509Certificate2Collection(c.Extra);
                foreach (var link in pending.Select(p => For(p.Element)!).Distinct().SelectMany(s => s.Chain)) extra.Add(link);
                return (c.End, extra);
            }).ToList();
            var found = RevocationInfo.Collect(chains, revocation, when, cancel);
            // what the new CMS blocks carry is not repeated in /DSS — only what a checker cannot find there; the root stays (also an own
            // certificate, which is its own root), as do the responders' certificates (EN 319 142-1, 6.3; No. 534); older and foreign
            // signatures keep theirs (AddArchiveTimestamp)
            var carried = made.SelectMany(LongTermValidation.CertificatesOf).Select(Convert.ToBase64String).ToHashSet(StringComparer.Ordinal);
            found.Certificates.RemoveAll(c => carried.Contains(Convert.ToBase64String(c)) && !RevocationInfo.SelfIssued(c));
            using (var doc = PdfDocument.OpenWritten(SaveBuffer.Source(file), security))
                file.Write(LongTermValidation.Update(doc, found));
            result = result with { LongTermMissing = found.Missing };
        }
        if (archive && timestamps is not null && made.Count > 0)
            result = result with { LongTermMissing = [.. result.LongTermMissing, .. AppendArchive(file, timestamps, revocation, when, security, cancel)] };
        return PdfFileSaver.Save(path, output =>
        {
            output.Write(file.GetBuffer(), 0, (int)file.Length);
            return result with { Length = file.Length };
        });
    }

    /// <summary>
    /// Appends a document time stamp to the bytes in the buffer and, with a source for the revocation status, the validation data of its
    /// service after it (so the time stamp itself stays checkable).
    /// </summary>
    /// <returns>Certificates whose revocation status could not be obtained.</returns>
    private static List<string> AppendArchive(MemoryStream file, ITimestampAuthority timestamps, IRevocationSource? revocation, DateTimeOffset when,
        Security.StandardSecurityHandler? security, CancellationToken cancel)
    {
        byte[] stamp;
        using (var doc = PdfDocument.OpenWritten(SaveBuffer.Source(file), security))
            stamp = PdfSigner.DocumentTimestampUpdate(doc, timestamps, cancel);
        file.Write(stamp);
        if (revocation is null) return [];
        using var stamped = PdfDocument.OpenWritten(SaveBuffer.Source(file), security);
        var token = DigitalSignatures.Find(stamped).OrderBy(e => e.ByteRange[2] + e.ByteRange[3]).Last();
        if (token.Signature?.GetString(PdfName.Contents)?.Bytes is not { } block || CmsSignature.Der(block) is not { } der) return [];
        var found = RevocationInfo.Collect(LongTermValidation.ChainsOf(der), revocation, when, cancel);
        file.Write(LongTermValidation.Update(stamped, found));
        return found.Missing;
    }

    /// <summary>
    /// Renews the long-term validation of a signed file (PAdES B-LTA, ETSI EN 319 142-1, 5.5): appends the validation data of all its signatures
    /// and document time stamps as they are now, then a new document time stamp, then its own validation data — before the certificates of
    /// the earlier time-stamping authority expire, so the file stays checkable for decades. The earlier signatures stay valid (only appended).
    /// </summary>
    /// <param name="main">The signed file.</param>
    /// <param name="path">Where to save (may be the file itself).</param>
    /// <param name="timestamps">The time-stamping authority.</param>
    /// <param name="revocation">Where to ask for the revocation status; null to add only the time stamp.</param>
    /// <param name="cancel">Cancels; the target stays unchanged.</param>
    /// <returns>The result; <see cref="PdfSaveResult.LongTermMissing"/> lists what could not be obtained.</returns>
    /// <exception cref="PdfException">The file cannot take an incremental update (encrypted or repaired).</exception>
    /// <exception cref="TimestampException">No time stamp could be obtained; nothing is written.</exception>
    /// <exception cref="CertificateRevokedException">A certificate of the file is revoked; nothing is written.</exception>
    public static PdfSaveResult AddArchiveTimestamp(PdfDocument main, string path, ITimestampAuthority timestamps, IRevocationSource? revocation,
        CancellationToken cancel = default)
    {
        if (!IncrementalUpdate.CanAppend(main))
            throw new PdfException(IncrementalUpdate.EncryptionRefusal(main) ?? Tr.T("An diese Datei lässt sich nichts anhängen."));
        var when = DateTimeOffset.Now;
        using var file = SaveBuffer.Create(main.FileSize, 256 * 1024);
        for (long at = 0; at < main.FileSize; at += 1 << 20) file.Write(main.FileBytes(at, 1 << 20));
        var missing = new List<string>();
        if (revocation is not null)
        {
            var blocks = DigitalSignatures.Find(main).Select(e => e.Signature?.GetString(PdfName.Contents)?.Bytes).OfType<byte[]>()
                .Select(CmsSignature.Der).OfType<byte[]>().ToList();
            // the certificates the file's validation data hold already (the roots, which the signature blocks leave out) help build the chains
            var stored = new X509Certificate2Collection();
            foreach (var raw in LongTermValidation.Read(main)?.Certificates ?? [])
                try
                {
                    stored.Add(X509CertificateLoader.LoadCertificate(raw));
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // an unreadable entry of the store
                }
            var chains = new List<(X509Certificate2, X509Certificate2Collection)>();
            foreach (var block in blocks)
            {
                try
                {
                    chains.AddRange(LongTermValidation.ChainsOf(block).Select(c =>
                    {
                        var extra = new X509Certificate2Collection(c.Extra);
                        extra.AddRange(stored);
                        return (c.End, extra);
                    }));
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // an unreadable signature block: it has no chain to fetch for
                }
            }
            var found = RevocationInfo.Collect(chains, revocation, when, cancel);
            using (var doc = PdfDocument.OpenWritten(SaveBuffer.Source(file), main.Security))
                file.Write(LongTermValidation.Update(doc, found));
            missing.AddRange(found.Missing);
        }
        missing.AddRange(AppendArchive(file, timestamps, revocation, when, main.Security, cancel));
        var result = new PdfSaveResult(main.Version, file.Length, 0, false) { LongTermMissing = missing };
        return PdfFileSaver.Save(path, output =>
        {
            output.Write(file.GetBuffer(), 0, (int)file.Length);
            return result;
        });
    }
}
