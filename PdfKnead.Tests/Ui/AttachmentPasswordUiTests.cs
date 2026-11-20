using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Attachments encrypted with a password of their own (the document itself opens without one): before redacting and on saving the program
/// asks for that password, then searches the attachments and carries them over. Before, saving broke off for good and the search could
/// only report them as not searchable (decided on 4.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 459).
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class AttachmentPasswordUiTests
{
    private static readonly byte[] Content = "Anlage: Erika Musterfrau, Kontonummer 4711"u8.ToArray();

    /// <summary>Answers the question for the password in turn and records whether each came after a wrong one.</summary>
    private sealed class Passwords(params string?[] answers)
    {
        private int _next;
        public List<bool> Wrong { get; } = [];

        public Task<string?> Ask(string fileName, bool wrong)
        {
            Wrong.Add(wrong);
            return Task.FromResult(_next < answers.Length ? answers[_next++] : null);
        }
    }

    [Fact]
    public void LockedAttachment_PasswordGiven_SearchedBeforeRedacting()
    {
        var path = TestFiles.Write(CryptFilterTests.WithLockedAttachment(Content, "Erika Musterfrau zahlt"), "anhang-gesperrt.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            await vm.OpenAsync([path]);
            var doc = vm.SelectedDocument!;
            await doc.RedactHitsAsync(await doc.FindForRedactionAsync(["Erika Musterfrau"], RedactionPatterns.None, false));
            // locked: only reported as not searchable
            var places = (await doc.FindElsewhereAsync()).Select(e => e.Where).ToList();
            Assert.DoesNotContain(Tr.T("Dateianhänge"), places);
            Assert.Contains(places, p => p.StartsWith(Tr.T("Dateianhänge, die nicht durchsucht werden konnten: {0}", "")[..20], StringComparison.Ordinal));

            var passwords = new Passwords("falsch", "Anhang");
            vm.AskAttachmentPassword = passwords.Ask;
            Assert.True(await vm.UnlockAttachmentsAsync(doc));
            Assert.Equal([false, true], passwords.Wrong);
            places = (await doc.FindElsewhereAsync()).Select(e => e.Where).ToList();
            Assert.Contains(Tr.T("Dateianhänge"), places);
            vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
        });
    }

    [Fact]
    public void LockedAttachment_OnSaving_PasswordAskedAndAttachmentKept_CancelledNothingSaved()
    {
        var path = TestFiles.Write(CryptFilterTests.WithLockedAttachment(Content), "anhang-speichern.pdf");
        var target = Path.Combine(Path.GetDirectoryName(path)!, "anhang-gespeichert.pdf");
        var cancelled = Path.Combine(Path.GetDirectoryName(path)!, "anhang-abgebrochen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            await vm.OpenAsync([path]);
            var doc = vm.SelectedDocument!;
            doc.RotateSelected(90);

            vm.AskAttachmentPassword = new Passwords([null]).Ask;
            Assert.False(await vm.SaveDocumentAsync(doc, cancelled, doc.SaveVersion));
            Assert.False(File.Exists(cancelled));
            Assert.Contains(Tr.T("Speichern fehlgeschlagen: {0}", "")[..10], vm.Message ?? "");

            var passwords = new Passwords("Anhang");
            vm.AskAttachmentPassword = passwords.Ask;
            Assert.True(await vm.SaveDocumentAsync(doc, target, doc.SaveVersion));
            Assert.Equal([false], passwords.Wrong);
            vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
        });
        using var saved = PdfDocument.Open(target);
        Assert.True(saved.Security!.AttachmentsLocked);
        Assert.Equal(PdfDocument.AttachmentUnlock.Opened, saved.UnlockAttachments("Anhang"));
        Assert.Contains(Enumerable.Range(1, saved.HighestObjectNumber).Select(n => saved.GetObject(n)),
            o => o is Core.Pdf.Objects.PdfStream s && s.Dictionary.IsType(Core.Pdf.Objects.PdfName.Get("EmbeddedFile")) && saved.DecodeStream(s).Data.SequenceEqual(Content));
    }
}
