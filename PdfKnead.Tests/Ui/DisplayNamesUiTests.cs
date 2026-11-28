using PdfKnead.Converters;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Security;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Names from an opened file in the user interface and on the command line (The Unicode Standard, 9, sentences 22–23 with UTR #36; question 74,
/// docs/FEHLERGEDAECHTNIS.md, No. 1217): where a deception touches security (file names of attachments, signer and certificate names, link
/// targets) every directional formatting character is shown as „⟨U+XXXX⟩“ and left out of a file name proposed for saving; every other name
/// (bookmarks, metadata, form fields, authors of comments) is shown between FSI and PDI. The names in the file stay as they are.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class DisplayNamesUiTests
{
    private const string Deceiving = "Rechnung\u202Efdp.exe";

    // ------------------------------------------------------------ the helper

    [Fact]
    public void Safe_ShowsEveryDirectionalFormattingCharacter_FileName_DropsThem()
    {
        Assert.Equal("Rechnung⟨U+202E⟩fdp.exe", DisplayNames.Safe(Deceiving));
        foreach (var c in new[] { 0x061C, 0x200E, 0x200F, 0x202A, 0x202B, 0x202C, 0x202D, 0x202E, 0x2066, 0x2067, 0x2068, 0x2069 })
        {
            var name = "a" + (char)c + "b";
            Assert.Equal($"a⟨U+{c:X4}⟩b", DisplayNames.Safe(name));
            Assert.Equal("ab", DisplayNames.FileName(name));
        }
        Assert.Equal("Rechnungfdp.exe", DisplayNames.FileName(Deceiving));
        // names without such characters, and right-to-left names, stay as they are
        Assert.Equal("Bericht.pdf", DisplayNames.Safe("Bericht.pdf"));
        Assert.Equal("\u05E9\u05DC\u05D5\u05DD.pdf", DisplayNames.Safe("\u05E9\u05DC\u05D5\u05DD.pdf"));
        Assert.Equal("", DisplayNames.Safe(null));
        Assert.Equal("", DisplayNames.FileName(null));
    }

    [Fact]
    public void Isolated_KeepsOpenEmbeddingsInside_RightToLeftUnchanged()
    {
        // a left-to-right name without such characters: as it is
        Assert.Equal("Kapitel 1", DisplayNames.Isolated("Kapitel 1"));
        // an override left open ends with the name
        Assert.Equal("\u2068Kapitel \u202E1\u2069", DisplayNames.Isolated("Kapitel \u202E1"));
        // a Hebrew bookmark with RLM: its characters unchanged, only isolated
        const string hebrew = "\u05E4\u05E8\u05E7 1\u200F";
        Assert.Equal("\u2068" + hebrew + "\u2069", DisplayNames.Isolated(hebrew));
        // a PDI without its initiator would close the FSI early: left out; an isolate left open is closed before the final PDI
        Assert.Equal("\u2068a\u202Eb\u2069", DisplayNames.Isolated("a\u2069\u202Eb"));
        Assert.Equal("\u2068a\u2067b\u2069\u2069", DisplayNames.Isolated("a\u2067b"));
        // the text after the name keeps its order: the levels of „x“ behind it are those without the name's override
        var line = DisplayNames.Isolated("Kapitel \u202E12") + " x";
        Assert.Equal(0, Core.Fonts.Layout.Bidi.Levels(line)[^1]);
        Assert.Equal(1, Core.Fonts.Layout.Bidi.Levels("Kapitel \u202E12 x")[^1] % 2);
    }

    [Fact]
    public void Bookmark_ShownIsolated_ThroughTheConverter()
    {
        // compared as strings (ordinal): compared as objects, the culture would pass over FSI and PDI
        var shown = (string?)IsolatedName.Instance.Convert("Kapitel \u202E1", typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("\u2068Kapitel \u202E1\u2069", shown);
        const string hebrew = "\u05E4\u05E8\u05E7\u200F";
        Assert.Equal("\u2068" + hebrew + "\u2069", (string?)IsolatedName.Instance.Convert(hebrew, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(IsolatedName.Instance.Convert(null, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------ attachments

    /// <summary>A wrapper (ISO 32000-2, 7.6.7) whose payload attachment has the name given.</summary>
    private static byte[] Wrapper(byte[] payload, string name)
    {
        var pdf = TestPdf.Simple(1);
        var text = new PdfString(PdfTextEncoding.Encode(name));
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("EmbeddedFile")), ("Subtype", TestPdf.N("application/pdf"))), payload));
        var spec = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Filespec")), ("F", text), ("UF", text), ("AFRelationship", TestPdf.N("EncryptedPayload")),
            ("EF", TestPdf.Dict(("F", TestPdf.Ref(file)))),
            ("EP", TestPdf.Dict(("Type", TestPdf.N("EncryptedPayload")), ("Subtype", TestPdf.N("MeinVerfahren")), ("Version", TestPdf.N("1"))))));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("AF"), new PdfArray([TestPdf.Ref(spec)]));
        catalog.Set(PdfName.Get("Names"), TestPdf.Dict(("EmbeddedFiles", TestPdf.Dict(("Names", new PdfArray([text, TestPdf.Ref(spec)]))))));
        return pdf.Build(version: "2.0");
    }

    /// <summary>
    /// The attachment „Rechnung U+202E fdp.exe“: the question shows the character (it cannot read „Rechnungexe.pdf“), the document opened from it is
    /// named — and proposed for saving — without it; the name in the file stays.
    /// </summary>
    [Fact]
    public void Attachment_QuestionShowsTheCharacter_ProposedNameWithoutIt()
    {
        var payload = TestPdf.Simple(2).Build(cipher: PdfCipher.Aes256, user: "geheim", owner: "chef", version: "2.0");
        var path = TestFiles.Write(Wrapper(payload, Deceiving), "huelle-name.pdf");
        using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
            Assert.Equal(Deceiving, EncryptedPayload.Of(doc)!.Name);
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            string? question = null, asked = null;
            vm.AskChoice = (_, text, _, _) =>
            {
                question = text;
                return Task.FromResult(1);
            };
            vm.AskPassword = (name, wrong) =>
            {
                asked = name;
                return Task.FromResult<string?>(wrong ? null : "geheim");
            };
            await vm.OpenAsync([path]);
            Assert.Contains("Rechnung⟨U+202E⟩fdp.exe", question);
            Assert.DoesNotContain('\u202E', question!);
            Assert.Equal("Rechnung⟨U+202E⟩fdp.exe", asked);
            Assert.Equal(2, vm.Documents.Count);
            Assert.Equal("Rechnungfdp.exe", vm.SelectedDocument!.FileName);
            Assert.DoesNotContain(vm.SelectedDocument.Path, c => Core.Fonts.Layout.Bidi.IsControl(c));
            foreach (var d in vm.Documents.ToList()) d.Dispose();
        });
    }

    // ------------------------------------------------------------ signatures

    private static SignatureCheck Check(string signer) =>
        new("Unterschrift\u202E1", SignatureOutcome.Invalid, null, LaterChanges.None, [], signer, null, "Freigabe\u202Eetc", "Ort\u2066", "adbe.pkcs7.detached",
            null, false, true, 0);

    /// <summary>The signer, field, reason and place of a signature: in the window and in the report with the characters shown.</summary>
    [Fact]
    public void Signature_SignerAndDetails_ShowTheCharacters()
    {
        var row = new SignatureRow(Check("Max\u202E Muster"));
        Assert.Equal("Max⟨U+202E⟩ Muster", row.Title);
        Assert.Contains(row.Details, d => d.Value == "Freigabe⟨U+202E⟩etc");
        Assert.Contains(row.Details, d => d.Value == "Ort⟨U+2066⟩");
        Assert.Contains(row.Details, d => d.Value == "Unterschrift⟨U+202E⟩1");
        Assert.DoesNotContain(row.Details, d => d.Value.Any(c => Core.Fonts.Layout.Bidi.IsControl(c)));
        var text = ValidationReport.ToText([Check("Max\u202E Muster")], "probe.pdf", "1.0", DateTimeOffset.UnixEpoch);
        Assert.Contains("Max⟨U+202E⟩ Muster", text);
        Assert.DoesNotContain(text, c => Core.Fonts.Layout.Bidi.IsControl(c));
        var html = ValidationReport.ToHtml([Check("Max\u202E Muster")], "probe.pdf", "1.0", DateTimeOffset.UnixEpoch);
        Assert.Contains("Max⟨U+202E⟩ Muster", html);
        Assert.DoesNotContain(html, c => Core.Fonts.Layout.Bidi.IsControl(c));
    }

    // ------------------------------------------------------------ links

    /// <summary>The target of a link in its tooltip: the address and a file name of another action with the characters shown.</summary>
    [Fact]
    public void Link_TargetInTheTooltip_ShowsTheCharacters()
    {
        var address = new AddressLink("https://example.org/\u202Efdp.exe", new AddressInfo("https://example.org/\u202Efdp.exe", null, null, null, []), false);
        Assert.Equal("https://example.org/⟨U+202E⟩fdp.exe", DocumentViewModel.Describe(address));
        var launch = DocumentViewModel.Describe(new OtherLink("Launch", Deceiving));
        Assert.Contains("Rechnung⟨U+202E⟩fdp.exe", launch);
        Assert.DoesNotContain(launch, c => Core.Fonts.Layout.Bidi.IsControl(c));
    }
}
