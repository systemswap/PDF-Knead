using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Platform;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Redacting a signed file (docs/RECHERCHE-VERSTECKEN.md, „Wiederherstellungswege“, older revisions): a signed file is otherwise saved with
/// the changes appended, the signed revision kept underneath. After redacting, the program no longer appends: the file is written anew, with
/// one revision, and the redacted word is nowhere in it — also not in the revision that was signed.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class RedactSignedUiTests
{
    [Fact]
    public void SignedFileRedacted_WrittenAnew_OneRevision_WordGone()
    {
        using var certificate = SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"schwaerzen-{Guid.NewGuid():N}.p12"));
        byte[] signed;
        using (var source = PdfDocument.Open(TestPdf.Simple(pages: 2).Build()))
            signed = PdfSigner.Sign(source, 1, new InkElement
            {
                Rect = new PdfRectangle(100, 100, 300, 180), Strokes = [[0, 20, 40, 70, 90, 10]], StrokeWidth = 3, Color = 0x1B3A8C,
                Digital = new DigitalSigning("Max Mustermann"),
            }, certificate, DateTimeOffset.Now);
        var path = TestFiles.Write(signed, "unterschrieben-schwaerzen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
            window.Show();
            try
            {
                await vm.OpenAsync([path]);
                var doc = vm.SelectedDocument!;
                Assert.True(doc.AppendsToSigned(null), "eine unveränderte signierte Datei würde angehängt gespeichert");
                var hits = await doc.FindForRedactionAsync(["Seite 1"], RedactionPatterns.None, false);
                await doc.RedactHitsAsync(hits);
                await doc.ApplyRedactionsAsync(false);
                Assert.False(doc.AppendsToSigned(null), "nach dem Schwärzen darf nichts mehr angehängt werden");
                var target = Path.Combine(Path.GetDirectoryName(path)!, "unterschrieben-geschwaerzt.pdf");
                await doc.SaveAsync(target, doc.SaveOptions("1.7"));
                var written = File.ReadAllBytes(target);
                Assert.Equal(1, Count(written, "%%EOF"u8));
                Assert.Equal(0, Count(written, "/Prev"u8));
                // the word in no stream, packed or not, and in no revision
                using var check = PdfDocument.Open(written);
                for (var n = 1; n <= check.HighestObjectNumber; n++)
                    if (check.HasObject(n) && check.GetObject(n) is Core.Pdf.Objects.PdfStream stream)
                        Assert.True(check.DecodeStream(stream).Data.AsSpan().IndexOf("Seite 1"u8) < 0, $"„Seite 1“ in Objekt {n}");
                Assert.True(written.AsSpan().IndexOf("Seite 1"u8) < 0, "„Seite 1“ in der Datei");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static int Count(byte[] data, ReadOnlySpan<byte> what)
    {
        var count = 0;
        for (var from = 0; data.AsSpan(from).IndexOf(what) is var at and >= 0; from += at + what.Length) count++;
        return count;
    }
}
