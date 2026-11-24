using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Document;

/// <summary>
/// The encrypted payload of an unencrypted wrapper document (ISO 32000-2, 7.6.7): the wrapper shows a cover page to readers without the
/// security handler of the payload; the payload itself is an embedded file whose file specification, named in the catalogue's /AF, says
/// /AFRelationship /EncryptedPayload and carries an encrypted payload dictionary /EP with the name of the cryptographic filter (/Subtype).
/// PDF-Knead recognises such a wrapper and offers to open the payload (design decision, 24.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 1115).
/// </summary>
/// <param name="Name">The file name of the payload (/UF, else /F).</param>
/// <param name="CryptographicFilter">/Subtype of /EP: the name of the cryptographic filter the payload is protected with.</param>
/// <param name="Version">/Version of /EP, if given.</param>
/// <param name="Data">The payload, a PDF file (decoded from its embedded file stream).</param>
public sealed record EncryptedPayload(string Name, string CryptographicFilter, string? Version, byte[] Data)
{
    /// <summary>The encrypted payload of a wrapper document; null if the document is none (or the payload cannot be read).</summary>
    /// <param name="doc">The document.</param>
    public static EncryptedPayload? Of(PdfDocument doc)
    {
        if (doc.Catalog?.GetArray(PdfName.Get("AF")) is not { } files) return null;
        for (var i = 0; i < Math.Min(files.Count, 64); i++)
        {
            if (files.Get(i) is not PdfDictionary spec || spec.GetName(PdfName.Get("AFRelationship"))?.Value != "EncryptedPayload"
                || spec.GetDictionary(PdfName.Get("EP")) is not { } ep || ep.GetName(PdfName.Get("Subtype"))?.Value is not { Length: > 0 } filter)
                continue;
            var ef = spec.GetDictionary(PdfName.Get("EF"));
            // a missing key reads as null object, not as null: /UF first, then /F
            if ((ef?.Get(PdfName.Get("UF")) is PdfStream unicode ? unicode : ef?.Get(PdfName.Get("F"))) is not PdfStream stream) continue;
            try
            {
                var data = doc.DecodeStream(stream).Data;
                var name = (spec.GetString(PdfName.Get("UF")) ?? spec.GetString(PdfName.Get("F")))?.ToText() is { Length: > 0 } n ? n : "payload.pdf";
                var version = ep.Get(PdfName.Get("Version")) switch
                {
                    PdfName v => v.Value,
                    PdfString s => s.ToText(),
                    _ => null,
                };
                return new EncryptedPayload(name, filter, version, data);
            }
            catch (PdfException)
            {
                // a payload that cannot be read: the wrapper is shown as it is
            }
        }
        return null;
    }
}
