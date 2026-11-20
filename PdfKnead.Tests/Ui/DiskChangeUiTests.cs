using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Open files changed by another program (docs/FEHLERGEDAECHTNIS.md, No. 240): changed — reload or keep; written anew in place (large
/// file) — reload or close; deleted — keep or close. The program's own saving is no change from outside.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class DiskChangeUiTests
{
    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", "aussen-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Records the questions and answers them in turn.</summary>
    private sealed class Answers(params int[] answers)
    {
        private int _next;
        public List<string> Headings { get; } = [];

        public Task<int> Ask(string heading, string message, string first, string? second)
        {
            Headings.Add(heading);
            return Task.FromResult(_next < answers.Length ? answers[_next++] : 0);
        }
    }

    [Fact]
    public void Changed_ReloadOrKeep_OwnSavingAsksNothing()
    {
        var path = Path.Combine(Folder(), "vertrag.pdf");
        File.WriteAllBytes(path, TestPdf.Simple(2).Build());
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            await vm.OpenAsync([path]);
            var doc = vm.SelectedDocument!;
            var answers = new Answers(2, 1);
            vm.AskChoice = answers.Ask;

            // nothing changed: no question
            await vm.CheckDiskAsync();
            Assert.Empty(answers.Headings);

            // changed by another program, „Behalten“: the opened version stays, no second question
            File.WriteAllBytes(path, TestPdf.Simple(3).Build());
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            await vm.CheckDiskAsync();
            Assert.Equal(Tr.T("„{0}“ wurde von einem anderen Programm geändert", "vertrag.pdf"), Assert.Single(answers.Headings));
            Assert.Same(doc, vm.SelectedDocument);
            Assert.Equal(2, doc.PageCount);
            await vm.CheckDiskAsync();
            Assert.Single(answers.Headings);

            // changed again, „Neu laden“: the new version in the same tab
            File.WriteAllBytes(path, TestPdf.Simple(4).Build());
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));
            await vm.CheckDiskAsync();
            Assert.Equal(2, answers.Headings.Count);
            var fresh = vm.SelectedDocument!;
            Assert.NotSame(doc, fresh);
            Assert.Equal(4, fresh.PageCount);
            Assert.Single(vm.Documents);

            // saved by the program itself: no change from outside
            fresh.RotateSelected(90);
            Assert.True(await vm.SaveDocumentAsync(fresh, path, fresh.SaveVersion));
            await vm.CheckDiskAsync();
            Assert.Equal(2, answers.Headings.Count);
        });
    }

    [Fact]
    public void Deleted_KeepThenCloseWithoutSaving()
    {
        var path = Path.Combine(Folder(), "anlage.pdf");
        File.WriteAllBytes(path, TestPdf.Simple(2).Build());
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            await vm.OpenAsync([path]);
            var answers = new Answers(1);
            vm.AskChoice = answers.Ask;
            File.Delete(path);
            await vm.CheckDiskAsync();
            Assert.Equal(Tr.T("„{0}“ gibt es nicht mehr", "anlage.pdf"), Assert.Single(answers.Headings));
            Assert.Single(vm.Documents);
            await vm.CheckDiskAsync();
            Assert.Single(answers.Headings);   // answered: not asked again
            // saved anew, the file is there again; deleted once more, asked again — „Reiter schließen“
            var doc = vm.SelectedDocument!;
            Assert.True(await vm.SaveDocumentAsync(doc, path, doc.SaveVersion));
            File.Delete(path);
            vm.AskChoice = new Answers(2).Ask;
            await vm.CheckDiskAsync();
            Assert.Empty(vm.Documents);
        });
    }

    [Fact]
    public void LargeFileWrittenInPlace_ReloadOrClose()
    {
        if (OperatingSystem.IsWindows()) return;   // a mapped file cannot be written over there
        var path = Path.Combine(Folder(), "plan.pdf");
        static byte[] Large(int pages)
        {
            var pdf = TestPdf.Simple(pages).Build();
            var data = new byte[pdf.Length + 6_000_000];
            pdf.CopyTo(data, 0);
            Array.Fill(data, (byte)' ', pdf.Length, data.Length - pdf.Length);
            return data;
        }
        File.WriteAllBytes(path, Large(2));
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            await vm.OpenAsync([path]);
            var answers = new Answers(1);
            vm.AskChoice = answers.Ask;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                stream.SetLength(0);
                stream.Write(Large(5));   // in place: the same file, new content
            }
            await vm.CheckDiskAsync();
            Assert.Equal(Tr.T("„{0}“ wurde von einem anderen Programm überschrieben", "plan.pdf"), Assert.Single(answers.Headings));
            Assert.Equal(5, vm.SelectedDocument!.PageCount);
        });
    }
}
