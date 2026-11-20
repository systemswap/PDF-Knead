using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// A digital signature placed into a prepared signature field of the file (docs/ABLAUFPLAN.md, 2.3): what the field locks after signing and
/// what its seed values require is said where the field is chosen — the signature window opened before, not knowing the field.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class PreparedFieldUiTests
{
    [Fact]
    public void PreparedFieldWithLockAndRequiredTimeStamp_ToldOnPlacing()
    {
        var file = PreparedFieldLockTests.Prepared(PreparedFieldLockTests.Lock("Include", PreparedFieldLockTests.Names("Name")),
            TestPdf.Dict(("Type", TestPdf.N("SV")), ("TimeStamp", TestPdf.Dict(("URL", new PdfString("http://zeitstempel.muster.example/"u8.ToArray())),
                ("Ff", PdfInteger.Of(1))))));
        var pdf = TestFiles.Write(file, "vorbereitetes-feld.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var messages = new List<string>();
                doc.MessageRequested += messages.Add;
                vm.FitPageCommand.Execute(null);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                vm.PickSignature = _ => Task.FromResult<PageElement?>(new SignatureElement
                {
                    Rect = default, Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf", Digital = new DigitalSigning("Max Mustermann"),
                });
                await vm.InsertSignatureCommand.ExecuteAsync(null);
                window.MouseDown(At(300, 170), MouseButton.Left);
                window.MouseUp(At(300, 170), MouseButton.Left);
                var placed = Assert.IsType<SignatureElement>(doc.ElementsOn(0).OfType<SignatureElement>().Single());
                Assert.Equal("Kunde", placed.Digital!.FieldName);
                var notice = Assert.Single(messages);
                Assert.Contains(Tr.T("Dieses Feld sperrt nach dem Unterschreiben: {0}.",
                    Core.Pdf.Signatures.SignatureTexts.Locked(new Core.Pdf.Signatures.FieldLock("Include", new HashSet<string> { "Name" }))), notice);
                Assert.Contains("http://zeitstempel.muster.example/", notice);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
