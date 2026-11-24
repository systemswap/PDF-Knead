using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The unencrypted wrapper document (ISO 32000-2, 7.6.7; design decision, 24.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 1115): opened, it is
/// named as a wrapper, and on „Dokument öffnen“ its payload opens as a document of its own with its password; „Abbrechen“ leaves the wrapper
/// alone; a payload PDF-Knead cannot open is said in the message bar.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class EncryptedPayloadUiTests
{
    /// <summary>A wrapper: a cover page, /Collection showing the payload, /AF with the file specification (EncryptedPayload, /EP).</summary>
    internal static byte[] Wrapper(byte[] payload, string filter = "MeinVerfahren")
    {
        var pdf = TestPdf.Simple(1);
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("EmbeddedFile")), ("Subtype", TestPdf.N("application/pdf"))), payload));
        var spec = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Filespec")), ("F", new PdfString("vertraulich.pdf"u8.ToArray())),
            ("UF", new PdfString("vertraulich.pdf"u8.ToArray())), ("AFRelationship", TestPdf.N("EncryptedPayload")),
            ("EF", TestPdf.Dict(("F", TestPdf.Ref(file)))),
            ("EP", TestPdf.Dict(("Type", TestPdf.N("EncryptedPayload")), ("Subtype", TestPdf.N(filter)), ("Version", TestPdf.N("1"))))));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("AF"), new PdfArray([TestPdf.Ref(spec)]));
        catalog.Set(PdfName.Get("Collection"), TestPdf.Dict(("D", new PdfString("vertraulich.pdf"u8.ToArray())), ("View", TestPdf.N("H"))));
        catalog.Set(PdfName.Get("Names"), TestPdf.Dict(("EmbeddedFiles", TestPdf.Dict(("Names", new PdfArray([new PdfString("vertraulich.pdf"u8.ToArray()), TestPdf.Ref(spec)]))))));
        return pdf.Build(version: "2.0");
    }

    [Fact]
    public void Core_WrapperRecognised_PayloadReadable()
    {
        var payload = TestPdf.Simple(2).Build(cipher: PdfCipher.Aes256, user: "geheim", owner: "chef", version: "2.0");
        using var doc = PdfDocument.Open(Wrapper(payload));
        var found = EncryptedPayload.Of(doc);
        Assert.NotNull(found);
        Assert.Equal("vertraulich.pdf", found.Name);
        Assert.Equal("MeinVerfahren", found.CryptographicFilter);
        Assert.Equal("1", found.Version);
        Assert.Equal(payload, found.Data);
        using var plain = PdfDocument.Open(TestPdf.Simple(1).Build());
        Assert.Null(EncryptedPayload.Of(plain));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void Wrapper_Opened_PayloadOpensOnRequestWithItsPassword(int answer, bool opens)
    {
        var payload = TestPdf.Simple(2).Build(cipher: PdfCipher.Aes256, user: "geheim", owner: "chef", version: "2.0");
        var path = TestFiles.Write(Wrapper(payload), "huelle.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            string? asked = null;
            vm.AskChoice = (title, _, _, _) =>
            {
                asked = title;
                return Task.FromResult(answer);
            };
            vm.AskPassword = (_, wrong) => Task.FromResult<string?>(wrong ? null : "geheim");
            await vm.OpenAsync([path]);
            Assert.Equal("Verschlüsseltes Dokument in einer Hülle", asked);
            Assert.Equal(opens ? 2 : 1, vm.Documents.Count);
            if (opens)
            {
                Assert.Equal("vertraulich.pdf", vm.SelectedDocument!.FileName);
                Assert.Equal(2, vm.SelectedDocument.Document.PageCount);
            }
            foreach (var d in vm.Documents.ToList()) d.Dispose();
        });
    }

    [Fact]
    public void Payload_WithAnUnknownHandler_SaidInTheMessageBar()
    {
        var payload = TestPdf.Simple(1).Build(cipher: PdfCipher.Aes256, user: "geheim", owner: "chef", version: "2.0",
            patchEncrypt: e => e.Set(PdfName.Filter, PdfName.Get("MeinVerfahren")));
        var path = TestFiles.Write(Wrapper(payload), "huelle-fremd.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel { AskChoice = (_, _, _, _) => Task.FromResult(1) };
            await vm.OpenAsync([path]);
            Assert.Single(vm.Documents);
            Assert.Contains("MeinVerfahren", vm.Message);
            foreach (var d in vm.Documents.ToList()) d.Dispose();
        });
    }
}
