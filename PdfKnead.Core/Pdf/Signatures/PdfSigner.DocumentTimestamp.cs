using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Signatures;

public static partial class PdfSigner
{
    /// <summary>
    /// Appends a document time stamp (ISO 32000-2:2020, 12.8.5; PAdES B-LTA, ETSI EN 319 142-1, 5.5): an invisible signature field whose
    /// value is a time-stamp token (RFC 3161) over all bytes of the file before it — a time-stamping authority confirms that the file, its
    /// signatures and their validation data existed then. Appended after the validation data, it keeps them checkable after the certificates
    /// of the signers expire; another one later renews it.
    /// </summary>
    /// <param name="doc">The document to append to; it must qualify for an incremental update.</param>
    /// <param name="authority">The time-stamping authority.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>The bytes to append to the document's bytes.</returns>
    /// <exception cref="TimestampException">No token could be obtained; nothing is appended.</exception>
    /// <exception cref="NotAppendableException">The document does not qualify for an incremental update.</exception>
    public static byte[] DocumentTimestampUpdate(PdfDocument doc, ITimestampAuthority authority, CancellationToken cancel = default) =>
        DocumentTimestampUpdate(doc, authority, Security.PdfMac.WritingOn, cancel);

    /// <inheritdoc cref="DocumentTimestampUpdate(PdfDocument, ITimestampAuthority, CancellationToken)"/>
    /// <param name="writeMac">Whether a MAC is written where the file asks for one (ISO/TS 32004, 6.5.2: attached to the time stamp token).</param>
    internal static byte[] DocumentTimestampUpdate(PdfDocument doc, ITimestampAuthority authority, bool writeMac, CancellationToken cancel = default)
    {
        for (var reserve = SignatureTimestamps.ReservedBytes; ; reserve *= 4)
        {
            cancel.ThrowIfCancellationRequested();
            var (bytes, gap, attach) = PrepareDocumentTimestamp(doc, reserve, writeMac);
            var token = SignatureTimestamps.TokenFor(Digest(doc, bytes, gap), authority, cancel);
            // the MAC of the revision on the time stamp token's signer (6.5.2: „digital signature or document time stamp signature“)
            if (attach) token = Security.PdfMac.AttachTo(token, Security.PdfMac.KekOf(doc), Digest(doc, bytes, gap));
            if (2 * token.Length > gap.End - gap.Start - 2)
            {
                if (reserve > 1 << 20) throw new TimestampException(Tr.T("Der Zeitstempel ist zu groß."));
                continue;
            }
            Encoding.ASCII.GetBytes(Convert.ToHexString(token)).CopyTo(bytes.AsSpan(gap.Start + 1));
            return bytes;
        }
    }

    /// <summary>The update with the signature dictionary of type /DocTimeStamp, its placeholder and an invisible field on the first page.</summary>
    private static (byte[] Bytes, (int Start, int End) Gap, bool Mac) PrepareDocumentTimestamp(PdfDocument doc, int reserve, bool writeMac)
    {
        if (doc.PageCount == 0 || doc.Pages[0].Id is not { } pageId) throw new NotSupportedException(Tr.T("Die Seite ist kein eigenes Objekt."));
        var update = new IncrementalUpdate(doc, writeMac);
        var sig = new PdfDictionary();
        sig.Set(PdfName.Type, PdfName.Get("DocTimeStamp"));
        sig.Set(PdfName.Filter, PdfName.Get("Adobe.PPKLite"));
        sig.Set(PdfName.Get("SubFilter"), PdfName.Get("ETSI.RFC3161"));
        sig.Set(ByteRange, new PdfArray { PdfInteger.Of(0), PdfInteger.Of(9_999_999_999), PdfInteger.Of(9_999_999_999), PdfInteger.Of(9_999_999_999) });
        sig.Set(PdfName.Contents, new PdfString(new byte[reserve], isHex: true));
        var sigRef = update.Add(sig);
        if (update.WritesMac) update.MacSignature = sigRef;

        // an invisible field (zero rectangle, printed and locked as signature fields are)
        var field = new PdfDictionary();
        field.Set(PdfName.Type, PdfName.Get("Annot"));
        field.Set(PdfName.Subtype, PdfName.Get("Widget"));
        field.Set(FieldType, PdfName.Get("Sig"));
        field.Set(Title, new PdfString(PdfTextEncoding.Encode(UniqueName(doc, Tr.T("Dokument-Zeitstempel"), Tr.T("Dokument-Zeitstempel") + " "))));
        field.Set(Value, sigRef);
        field.Set(Rect, new PdfArray { PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(0) });
        field.Set(Flags, PdfInteger.Of(4 | 128));
        field.Set(PageRef, new PdfReference(pageId));
        var fieldRef = update.Add(field);
        IncrementalParts.AddAnnotations(update, doc.Pages[0], pageId, [fieldRef]);

        var rootRef = (PdfReference)doc.Trailer.GetRaw(PdfName.Root)!;
        var catalog = IncrementalParts.Copy(doc.Catalog!);
        var acroFormRaw = doc.Catalog!.GetRaw(AcroForm);
        var acroForm = doc.Catalog.GetDictionary(AcroForm) is { } existing ? IncrementalParts.Copy(existing) : new PdfDictionary();
        var fieldsRaw = acroForm.GetRaw(Fields);
        var fields = acroForm.GetArray(Fields) is { } list ? new PdfArray(list) : new PdfArray();
        fields.Add(fieldRef);
        acroForm.Set(SigFlags, PdfInteger.Of(3));
        if (fieldsRaw is PdfReference fieldsRef) update.Replace(fieldsRef.Id, fields);
        else acroForm.Set(Fields, fields);
        var catalogChanged = PadesExtension.Declare(catalog, doc, PadesExtension.LongTerm);   // document timestamps before PDF 2.0
        if (acroFormRaw is PdfReference acroFormRef) update.Replace(acroFormRef.Id, acroForm);
        else
        {
            catalog.Set(AcroForm, acroForm);
            catalogChanged = true;
        }
        if (catalogChanged) update.Replace(rootRef.Id, catalog);

        using var output = new MemoryStream();
        update.WriteUpdateTo(output);
        var bytes = output.ToArray();
        var gap = Placeholder(bytes, update.Offsets[sigRef.Id.Number] - doc.FileSize);
        WriteRange(bytes, gap, doc.FileSize);
        return (bytes, gap, update.WritesMac);
    }
}
