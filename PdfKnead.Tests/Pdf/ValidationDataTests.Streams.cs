using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Signatures;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// A stream where the change check expects a dictionary — the value of a „time stamp“ field, the security store (/DSS) — never counts as
/// validation data (docs/FEHLERGEDAECHTNIS.md, No. 641). The check reads such values with <c>is PdfDictionary</c>, and a stream is no
/// <see cref="PdfDictionary"/> in the object model (it holds one): the extra tests <c>is PdfStream</c> beside them could never hold and
/// went (compiler warning CS0184, found in a Linux build). These tests hold what the type alone guarantees.
/// </summary>
public sealed partial class ValidationDataTests
{
    /// <summary>An invisible „time stamp“ field whose value is a stream with the entries of a time stamp: at P = 1 a change.</summary>
    [Fact]
    public void CertifiedNoChanges_StampFieldValueIsAStream_StaysDetected()
    {
        var file = Append(Signed(TestPdf.Simple(1).Build(), CertificationLevel.NoChanges), (doc, update) =>
        {
            var widget = StampLikeWidget(update, 0, 0);
            widget.Set(PdfName.Get("V"), update.Add(TestPdf.FlateStream([], TestPdf.Dict(("Type", TestPdf.N("DocTimeStamp")),
                ("Filter", TestPdf.N("Adobe.PPKLite")), ("SubFilter", TestPdf.N("ETSI.RFC3161"))))));
            AddWidget(doc, update, widget);
        });
        var check = SignatureOf(file);
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, check.Outcome);
        Assert.NotEqual(LaterChanges.ValidationData, check.Later);
    }

    /// <summary>A security store (/DSS) that is a stream — its lists in the stream dictionary: at P = 1 a change, no validation data.</summary>
    [Fact]
    public void CertifiedNoChanges_DssIsAStream_StaysDetected()
    {
        var file = WithCatalog(Signed(TestPdf.Simple(1).Build(), CertificationLevel.NoChanges), (catalog, update) =>
            catalog.Set(PdfName.Get("DSS"), update.Add(TestPdf.FlateStream("BT /Helv 10 Tf (Erika Mustermann) Tj ET"u8.ToArray(),
                TestPdf.Dict(("Certs", new PdfArray()), ("VRI", new PdfDictionary()))))));
        var check = SignatureOf(file);
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, check.Outcome);
        Assert.NotEqual(LaterChanges.ValidationData, check.Later);
    }
}
