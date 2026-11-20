using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;
using static PdfKnead.Tests.Pdf.ContentImageTests;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// „Bilder bearbeiten“ with prepared files (KONZEPT.md, section 5.20, analysis round): broken references, forms that invoke themselves,
/// unbalanced q/Q, matrices that overflow or cannot be inverted, names with special characters, resources inherited from the page tree. Nothing
/// may hang or fail, nothing but finite numbers and correctly written names may land in the content, and what is not edited stays as it was.
/// </summary>
public sealed partial class ContentEditCraftedTests
{
    private static (PdfDocument Doc, PageItem Item, PageObjects Found) Page(TestPdf pdf)
    {
        var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        return (doc, item, ContentObjectEditor.ObjectsOf(item));
    }

    /// <summary>Every number in a content stream, as written.</summary>
    [GeneratedRegex(@"(?<![A-Za-z#/])[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?")]
    private static partial Regex Number();

    /// <summary>The decoded content of a page and of every form it invokes (also in forms).</summary>
    private static string Contents(PdfDocument doc, PdfDictionary page)
    {
        var text = new StringBuilder();
        void Add(PdfDictionary? resources, int depth)
        {
            if (depth > 4 || resources?.GetDictionary(PdfName.Get("XObject")) is not { } xobjects) return;
            foreach (var (name, _) in xobjects.Entries)
                if (xobjects.Get(name) is PdfStream { } form && form.Dictionary.GetName(PdfName.Subtype)?.Value == "Form")
                {
                    text.Append(Encoding.Latin1.GetString(doc.GetStreamData(form))).Append('\n');
                    Add(form.Dictionary.GetDictionary(PdfName.Resources), depth + 1);
                }
        }
        if (page.Get(PdfName.Contents) is PdfStream content) text.Append(Encoding.Latin1.GetString(content.RawData ?? doc.GetStreamData(content))).Append('\n');
        Add(page.GetDictionary(PdfName.Resources), 0);
        return text.ToString();
    }

    /// <summary>Content with a name that is in no resources and a form that invokes itself: found without them, edited and saved.</summary>
    [Fact]
    public void BrokenReferenceAndFormInvokingItself_EditedAndSaved()
    {
        var pdf = TestPdf.Simple(1);
        var formNumber = pdf.Add(new PdfDictionary());
        pdf.Replace(formNumber, new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 100, 100)),
            ("Resources", TestPdf.Dict(("XObject", TestPdf.Dict(("Self", TestPdf.Ref(formNumber))))))), "1 0 0 rg 10 10 30 30 re f /Self Do"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("F", TestPdf.Ref(formNumber))))));
        pdf.Replace(4, TestPdf.FlateStream("/Missing Do q 1 0 0 1 100 500 cm /F Do Q 0 0 1 rg 300 300 40 40 re f"u8.ToArray()));
        var (doc, item, found) = Page(pdf);
        using (doc)
        {
            Assert.Equal(2, found.Count);   // the square in the form (once: its own invocation is not followed) and the blue one
            var moved = ContentObjectEditor.Transform(item, [found.KeyOf(0), found.KeyOf(1)], Matrix.Translate(50, -50), found);
            using var output = new MemoryStream();
            PdfWriter.Write(doc, [moved], output);
            using var saved = PdfDocument.Open(output.ToArray());
            using var bitmap = Render(saved, saved.Pages[0]);
            Assert.True(Red(At(bitmap, saved.Pages[0], 175, 475)), "das Quadrat im Formular verschoben");
            Assert.Equal(2, ContentObjects.Find(saved, saved.Pages[0]).Count);
        }
    }

    /// <summary>
    /// Unbalanced q/Q (more Q than q at the start, an open q at the end): the drawings found and moved, what follows the edited one keeps its
    /// place.
    /// </summary>
    [Fact]
    public void UnbalancedSaveAndRestore_MovedAndTheRestStays()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("Q Q 1 0 0 rg 100 100 50 50 re f Q q 0 0 1 rg 300 100 50 50 re f q"u8.ToArray()));
        var (doc, item, found) = Page(pdf);
        using (doc)
        {
            Assert.Equal(2, found.Count);
            var red = Enumerable.Range(0, found.Count).Single(i => found.VisibleOf(i).Left == 100);
            var moved = ContentObjectEditor.Transform(item, [found.KeyOf(red)], Matrix.Translate(0, 300), found);
            using var bitmap = Render(doc, moved.Page);
            Assert.True(Red(At(bitmap, moved.Page, 125, 425)));
            Assert.Equal(SKColors.White, At(bitmap, moved.Page, 125, 125));
            Assert.True(At(bitmap, moved.Page, 325, 125) is { Blue: > 180, Red: < 80 }, "das Blaue bleibt");
        }
    }

    /// <summary>
    /// Matrices that overflow or cannot be inverted — in the content and as the edit: nothing but finite numbers in what is written, objects
    /// with a CTM that cannot be inverted (they draw nothing) stay as they are, and the edit with a matrix that overflows changes nothing.
    /// </summary>
    [Fact]
    public void OverflowingAndSingularMatrices_OnlyFiniteNumbersWritten()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes(
            "q 1e200 0 0 1e200 0 0 cm 0 0 1 1 re f Q " +            // overflows when moved
            "q 0 0 0 0 100 100 cm 0 0 10 10 re f Q " +              // cannot be inverted
            "q 1 0 0 1 0 0 cm 1 0 0 rg 200 200 50 50 re f Q")));
        var (doc, item, found) = Page(pdf);
        using (doc)
        {
            var keys = Enumerable.Range(0, found.Count).Select(found.KeyOf).ToList();
            var edited = ContentObjectEditor.Transform(item, keys, new Matrix(1e300, 0, 0, 1e300, 1e300, -1e300), found);
            edited = ContentObjectEditor.Transform(edited, keys, Matrix.Translate(1e308, 0), found.WithEdits(edited.ObjectEdits));
            edited = ContentObjectEditor.Transform(edited, keys, Matrix.Rotate(33), found.WithEdits(edited.ObjectEdits));
            var text = Contents(doc, edited.Page.Dictionary);
            Assert.DoesNotContain("NaN", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Infinity", text, StringComparison.OrdinalIgnoreCase);
            foreach (Match m in Number().Matches(text))
                Assert.True(double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v), m.Value);
            using var output = new MemoryStream();
            PdfWriter.Write(doc, [edited], output);
            using var saved = PdfDocument.Open(output.ToArray());
            Assert.False(saved.WasRepaired);
        }
    }

    /// <summary>
    /// A form whose box becomes infinite on the page (a matrix that overflows): whether its objects reach beyond the box cannot be computed
    /// (infinity minus infinity) — moved without failing (found by fuzzing, docs/FEHLERGEDAECHTNIS.md, No. 338).
    /// </summary>
    [Fact]
    public void FormBoxOverflowing_ObjectsMovedWithoutFailing()
    {
        var pdf = TestPdf.Simple(1);
        var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")),
            ("BBox", TestPdf.Box(-1e300, -1e300, 1e300, 1e300)), ("Matrix", TestPdf.Box(1e300, 0, 0, 1e300, 0, 0))), "1 0 0 rg 0 0 1 1 re f"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Fm", TestPdf.Ref(form))))));
        pdf.Replace(4, TestPdf.FlateStream("/Fm Do 0 0 1 rg 100 100 50 50 re f"u8.ToArray()));
        var (doc, item, found) = Page(pdf);
        using (doc)
        {
            var keys = Enumerable.Range(0, found.Count).Select(found.KeyOf).ToList();
            var edited = ContentObjectEditor.Transform(item, keys, Matrix.Translate(10, 10), found);
            using var output = new MemoryStream();
            PdfWriter.Write(doc, [edited], output);
            using var saved = PdfDocument.Open(output.ToArray());
            Assert.False(saved.WasRepaired);
        }
    }

    /// <summary>
    /// An image under a name with characters that need escaping, its resources inherited from the page tree: moved and deleted, saved, read
    /// back — the name written correctly, the image where it should be, and gone after deleting.
    /// </summary>
    [Fact]
    public void ImageWithStrangeNameFromInheritedResources_MovedAndDeleted()
    {
        var pdf = TestPdf.Simple(1);
        var image = pdf.Add(Solid(pdf, 220, 30, 30, 4));
        var name = "Im (1)/#2 Ä";
        var pages = (PdfDictionary)pdf.Get(2);
        pages.GetDictionary(PdfName.Resources)!.Set(PdfName.Get("XObject"), TestPdf.Dict((name, TestPdf.Ref(image))));
        var content = new MemoryStream();
        content.Write("q 100 0 0 100 100 600 cm "u8);
        PdfSerializer.Write(PdfName.Get(name), content);
        content.Write(" Do Q"u8);
        pdf.Replace(4, TestPdf.FlateStream(content.ToArray()));
        var (doc, item, found) = Page(pdf);
        using (doc)
        {
            var photo = Assert.Single(found);
            Assert.Equal(ContentKind.Image, photo.Kind);
            var moved = ContentObjectEditor.Transform(item, [photo.Key], Matrix.Translate(200, -300), found);
            using (var output = new MemoryStream())
            {
                PdfWriter.Write(doc, [moved], output);
                using var saved = PdfDocument.Open(output.ToArray());
                using var bitmap = Render(saved, saved.Pages[0]);
                Assert.True(Red(At(bitmap, saved.Pages[0], 350, 350)));
                Assert.Equal(SKColors.White, At(bitmap, saved.Pages[0], 150, 650));
            }
            var deleted = ContentObjectEditor.Delete(item, [photo.Key], found);
            using (var output = new MemoryStream())
            {
                PdfWriter.Write(doc, [deleted], output);
                using var saved = PdfDocument.Open(output.ToArray());
                Assert.Empty(ContentObjects.Find(saved, saved.Pages[0]));
                using var bitmap = Render(saved, saved.Pages[0]);
                Assert.Equal(SKColors.White, At(bitmap, saved.Pages[0], 150, 650));
            }
        }
    }

    /// <summary>
    /// A photo moved and then redacted where it now lies: the pixels under the bar leave the file — no image in it still holds the photo's
    /// colour, and none at its old place either.
    /// </summary>
    [Fact]
    public void MovedThenRedacted_PixelsGoneFromTheFile()
    {
        var pdf = TestPdf.Simple(1);
        var image = pdf.Add(Solid(pdf, 30, 30, 220, 8));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Foto", TestPdf.Ref(image))))));
        pdf.Replace(4, TestPdf.FlateStream("q 80 0 0 80 100 600 cm /Foto Do Q"u8.ToArray()));
        var (doc, item, found) = Page(pdf);
        using (doc)
        {
            var photo = Assert.Single(found);
            var moved = ContentObjectEditor.Transform(item, [photo.Key], Matrix.Translate(250, -400), found);
            moved = moved.WithElements([new RedactionElement { Rect = new PdfRectangle(340, 190, 440, 290) }]);
            using var output = new MemoryStream();
            PdfWriter.Write(doc, [moved], output, new PdfSaveOptions { ApplyRedactions = true, ImageCodec = SkiaImageCodec.Instance });
            using var saved = PdfDocument.Open(output.ToArray());
            var blue = 0;
            for (var n = 1; n < saved.ObjectCount; n++)
                if (saved.GetObject(n) is PdfStream s && s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image" &&
                    PdfImageDecoder.Decode(new ImageSource(s, null, null, false, 0), saved) is { } decoded)
                    for (var k = 0; k < decoded.Width * decoded.Height; k++)
                        if (decoded.Format == ImagePixels.Rgba && decoded.Pixels[k * 4 + 2] > 180 && decoded.Pixels[k * 4] < 80) blue++;
            Assert.Equal(0, blue);
            using var bitmap = Render(saved, saved.Pages[0]);
            Assert.Equal(SKColors.White, At(bitmap, saved.Pages[0], 140, 640));   // nothing at its old place
        }
    }
}
