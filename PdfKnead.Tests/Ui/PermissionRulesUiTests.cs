using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The rights of the standard security handler strictly as ISO 32000-2, 7.6.4.2, Table 22 states them (docs/FEHLERGEDAECHTNIS.md, No. 1111):
/// bit 4 changes the content and assembles pages, bit 6 adds annotations and fills forms, bits 4 and 6 together create form fields, from
/// revision 3 bit 9 alone fills forms and bit 11 alone assembles pages; with revision 2 bits 9 to 12 do not count. Each case checks the
/// rights and the actions that ask for them. The files open for the user, not the owner.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class PermissionRulesUiTests
{
    /// <summary>Opens the file as user in a document view, hands it to <paramref name="check"/>, then releases it.</summary>
    private static void WithDocument(byte[] file, int revision, Action<DocumentViewModel> check)
    {
        var path = TestFiles.Write(file, "rechte-tabelle-22.pdf");
        UiSession.Run(async () =>
        {
            var doc = new DocumentViewModel(path, PdfDocument.Open(path));
            try
            {
                Assert.False(doc.Document.Security!.IsOwner);
                Assert.Equal(revision, doc.Document.Security.Revision);
                Assert.False(doc.HasAllRights);
                check(doc);
            }
            finally
            {
                await doc.Renderer.WaitUntilUnused([doc.Document]).WaitAsync(TimeSpan.FromSeconds(10));
                doc.Dispose();
            }
        });
    }

    /// <summary>A file of revision 3 (RC4 128 bit) granting <paramref name="permissions"/> (bit 10 is always added by the program).</summary>
    private static byte[] Revision3With(PdfPermissions permissions) =>
        TestPdf.Simple(1).Build(cipher: PdfCipher.Rc4, user: "", permissions: permissions);

    /// <summary>The rights and the actions behind them: content, pages, annotations, filling in, form design.</summary>
    private static void AssertRights(DocumentViewModel doc, bool content, bool pages, bool annotate, bool fill, bool design)
    {
        Assert.Equal(content, doc.CanModifyContent);
        Assert.Equal(pages, doc.CanEditPages);
        Assert.Equal(annotate, doc.CanAnnotate);
        Assert.Equal(fill, doc.CanFillForms);
        Assert.Equal(design, doc.CanDesignForms);
        // actions by what they write: text of the page content, a note (annotation), a new form field
        var rect = new PdfRectangle(20, 20, 120, 40);
        Assert.Equal(content, doc.CanChange(new RichTextElement { Rect = rect, Content = new RichText("Inhalt", new RichStyle { Family = "Liberation Sans", Size = 12 }) }));
        Assert.Equal(annotate, doc.CanChange(new ShapeElement { Rect = rect }));
        Assert.Equal(annotate, doc.CanPlace(ViewTool.InsertNote));
        Assert.Equal(design, doc.CanPlace(ViewTool.InsertFormField));
        Assert.Equal(design, doc.CanChange(new FieldElement { Rect = rect, Spec = new FieldSpec { Name = "Feld" } }));
        Assert.Equal(annotate, doc.AddNote(0, 50, 50) is not null);
        Assert.Equal(design, doc.CreateField(0, rect, 0) is not null);
    }

    /// <summary>Revision 3, bit 4 alone: content and pages yes (bit 4 includes assembling), annotations and filling in no (Table 22, bits 6 and 9).</summary>
    [Fact]
    public void Revision3_OnlyBit4_ContentAndPages_NoAnnotationsNoFilling()
    {
        WithDocument(Revision3With(PdfPermissions.Modify), 3, doc =>
            AssertRights(doc, content: true, pages: true, annotate: false, fill: false, design: false));
    }

    /// <summary>Bit 6 alone: annotations and filling in yes; creating form fields no (that needs bit 4 as well), the content no.</summary>
    [Fact]
    public void OnlyBit6_AnnotationsAndFilling_NoFormFieldsNoContent()
    {
        WithDocument(Revision3With(PdfPermissions.Annotate), 3, doc =>
            AssertRights(doc, content: false, pages: false, annotate: true, fill: true, design: false));
    }

    /// <summary>Bits 4 and 6: creating and changing form fields yes (Table 22, bit 6: „if bit 4 is also set“).</summary>
    [Fact]
    public void Bits4And6_FormFieldsMayBeCreated()
    {
        WithDocument(Revision3With(PdfPermissions.Modify | PdfPermissions.Annotate), 3, doc =>
            AssertRights(doc, content: true, pages: true, annotate: true, fill: true, design: true));
    }

    /// <summary>
    /// Revision 3, bit 9 alone: filling in yes (signature fields included), nothing else. Signing is filling in a signature field: with bit 9
    /// alone it is not refused (an AES-128 file of revision 4, as RC4 files are not signed at all).
    /// </summary>
    [Fact]
    public void Revision3_OnlyBit9_FillingOnly()
    {
        WithDocument(Revision3With(PdfPermissions.FillForms), 3, doc =>
            AssertRights(doc, content: false, pages: false, annotate: false, fill: true, design: false));
        WithDocument(TestPdf.Simple(1).Build(cipher: PdfCipher.Aes128, user: "", permissions: PdfPermissions.FillForms), 4, doc =>
        {
            AssertRights(doc, content: false, pages: false, annotate: false, fill: true, design: false);
            Assert.Null(doc.DigitalSigningRefusal);
        });
    }

    /// <summary>Revision 3, bit 11 alone: assembling pages yes, nothing else.</summary>
    [Fact]
    public void Revision3_OnlyBit11_PagesOnly()
    {
        WithDocument(Revision3With(PdfPermissions.Assemble), 3, doc =>
            AssertRights(doc, content: false, pages: true, annotate: false, fill: false, design: false));
    }

    /// <summary>
    /// Revision 2 with bits 9 to 12 set and bits 4 and 6 cleared: neither filling in nor assembling pages — revision 2 does not know those
    /// bits, they are not asked (Table 22). File built independently in the test (Algorithms 2 to 4).
    /// </summary>
    [Fact]
    public void Revision2_Bits9To12Set_Bits4And6Cleared_NoFillingNoPages()
    {
        // bits 3, 5 and 7 to 32 set, bits 4 and 6 cleared
        WithDocument(EncryptionNormTests.Revision2File(unchecked((int)0xFFFFFFD4)), 2, doc =>
        {
            Assert.True(doc.Document.Security!.Permissions.HasFlag(PdfPermissions.FillForms | PdfPermissions.Assemble));   // the file sets them
            AssertRights(doc, content: false, pages: false, annotate: false, fill: false, design: false);
        });
    }

    // ------------------------------------------------------------ „Sicher“ tab: flattening, saving as images, cleaning, redacting

    /// <summary>The message of every action the rights forbid.</summary>
    private static string Refusal => Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen.");

    /// <summary>Opens a file of revision 3 with <paramref name="permissions"/> in the main window as user and hands it to <paramref name="check"/>.</summary>
    private static void WithMainWindow(PdfPermissions permissions, Func<MainViewModel, DocumentViewModel, Task> check)
    {
        var path = TestFiles.Write(Revision3With(permissions), "rechte-sicher.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([path]);
                var doc = vm.SelectedDocument!;
                Assert.Equal(3, doc.Document.Security!.Revision);
                Assert.False(doc.HasAllRights);
                await check(vm, doc);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>„Flachlegen“ on saving writes fields, comments and stamps into the page content: only with bit 4, otherwise refused with the message.</summary>
    [Theory]
    [InlineData(PdfPermissions.Print | PdfPermissions.Copy | PdfPermissions.Annotate | PdfPermissions.FillForms | PdfPermissions.Assemble, false)]
    [InlineData(PdfPermissions.Print | PdfPermissions.Modify, true)]
    public void FlattenOnSave_OnlyWithBit4(PdfPermissions permissions, bool allowed)
    {
        WithMainWindow(permissions, (vm, doc) =>
        {
            vm.FlattenOnSave = true;
            Assert.Equal(allowed, doc.FlattenOnSave);
            Assert.Equal(allowed, vm.FlattenOnSave);
            if (!allowed) Assert.Equal(Refusal, vm.Message);
            return Task.CompletedTask;
        });
    }

    /// <summary>„Flachlegen als Bild“ turns the pages into images: only with bit 4; without it the settings are not even asked for.</summary>
    [Theory]
    [InlineData(PdfPermissions.Print | PdfPermissions.Copy | PdfPermissions.Annotate | PdfPermissions.FillForms | PdfPermissions.Assemble, false)]
    [InlineData(PdfPermissions.Print | PdfPermissions.Modify, true)]
    public void RasterOnSave_OnlyWithBit4(PdfPermissions permissions, bool allowed)
    {
        WithMainWindow(permissions, async (vm, doc) =>
        {
            var asked = false;
            vm.PickRaster = () =>
            {
                asked = true;
                return Task.FromResult<PdfKnead.Rendering.RasterSettings?>(new PdfKnead.Rendering.RasterSettings());
            };
            await vm.ToggleRasterCommand.ExecuteAsync(null);
            Assert.Equal(allowed, asked);
            Assert.Equal(allowed, doc.RasterOnSave is not null);
            if (!allowed) Assert.Equal(Refusal, vm.Message);
        });
    }

    /// <summary>„Dokument bereinigen“ removes parts of the document: only with bit 4; without it the area does not open.</summary>
    [Theory]
    [InlineData(PdfPermissions.Print | PdfPermissions.Copy | PdfPermissions.Annotate | PdfPermissions.FillForms | PdfPermissions.Assemble, false)]
    [InlineData(PdfPermissions.Print | PdfPermissions.Modify, true)]
    public void CleanDocument_OnlyWithBit4(PdfPermissions permissions, bool allowed)
    {
        WithMainWindow(permissions, async (vm, doc) =>
        {
            await vm.CleanDocumentCommand.ExecuteAsync(null);
            Assert.Equal(allowed, doc.IsCleanupOpen);
            if (!allowed) Assert.Equal(Refusal, vm.Message);
        });
    }

    /// <summary>
    /// „Schwärzungen anwenden“ removes content (bit 4) and the marks, which are annotations (bit 6): only with both. A mark already on the page
    /// (here put there directly, as one saved earlier by the owner) stays a mark without bit 4 — and nothing goes on unredacted.
    /// </summary>
    [Theory]
    [InlineData(PdfPermissions.Print | PdfPermissions.Copy | PdfPermissions.Annotate | PdfPermissions.FillForms | PdfPermissions.Assemble, false)]
    [InlineData(PdfPermissions.Print | PdfPermissions.Modify | PdfPermissions.Copy | PdfPermissions.FillForms | PdfPermissions.Assemble, false)]   // bit 6 missing
    [InlineData(PdfPermissions.Print | PdfPermissions.Modify | PdfPermissions.Annotate, true)]
    public void ApplyRedactions_OnlyWithBits4And6(PdfPermissions permissions, bool allowed)
    {
        WithMainWindow(permissions, async (vm, doc) =>
        {
            doc.AddElement(0, new RedactionElement { Rect = new PdfRectangle(20, 20, 120, 60) }, Tr.T("Bereich schwärzen"));
            Assert.Equal(1, doc.RedactionCount);
            var asked = false;
            vm.ConfirmRedaction = _ =>   // the window would show its dialog: answered here
            {
                asked = true;
                return Task.FromResult(new RedactionReply(RedactionAnswer.Apply));
            };
            await vm.ApplyRedactionsCommand.ExecuteAsync(null);
            Assert.Equal(allowed, asked);
            Assert.Equal(allowed, doc.HasAppliedRedactions);
            Assert.Equal(allowed ? 0 : 1, doc.RedactionCount);
            if (!allowed) Assert.Equal(Refusal, vm.Message);
        });
    }
}
