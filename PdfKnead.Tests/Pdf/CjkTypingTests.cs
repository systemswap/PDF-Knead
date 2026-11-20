using System.Runtime.CompilerServices;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Tests that switch the folder of the CJK fonts (<see cref="CjkFonts.FolderForTests"/> is shared by all) run one after another — and alone:
/// the folder is the fallback for characters neither a font nor Liberation has, so switching it while another test redacts changed such a
/// character between the redaction and the check of the written file, and that check refused to save (RedactionLengthTests, seen in the
/// full run of 12.11.2026, reproduced with ReferenceFontTests, CjkTypingTests and CrossRoundTests beside it; docs/FEHLERGEDAECHTNIS.md, No. 774).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CjkFontsCollection
{
    public const string Name = "CJK-Schriften";
}

/// <summary>
/// Typed Chinese, Japanese and Korean characters (text editing): Noto CJK serves as the third fallback font and is saved as a
/// small CFF subset (CIDFontType0C) instead of placeholder boxes.
/// </summary>
[Collection(CjkFontsCollection.Name)]
public sealed class CjkTypingTests
{
    private static string Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    [Fact]
    public void TypedCjk_DrawnWithNoto_SavedAsSmallCffSubset()
    {
        CjkFonts.FolderForTests = Path.Combine(Repo(), "Schriften", "CJK");
        try
        {
            using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
            var element = RichLayout.Fitted(new RichTextElement
            {
                Rect = new PdfRectangle(72, 600, 500, 700),
                Content = new RichText("Max 日本語 한국어", new RichStyle { Family = "Liberation Sans", Size = 14 }),
            });
            var glyphs = RichLayout.Of(element).Lines.SelectMany(l => l.Glyphs).ToList();
            Assert.All(glyphs, g => Assert.NotEqual(0, g.Glyph));   // no placeholder glyph
            Assert.Contains(glyphs, g => g.Face?.BaseName.StartsWith("NotoSansCJK", StringComparison.Ordinal) == true);

            using var output = new MemoryStream();
            PdfWriter.Write(original, [PageItem.From(original)[0].WithElements([element])], output);
            if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)   // for cross-checking in other readers
                File.WriteAllBytes(Path.Combine(folder, "cjk-getippt.pdf"), output.ToArray());
            using var saved = PdfDocument.Open(output.ToArray());
            var text = PageText.Create(PageInterpreter.BuildText(saved, saved.Pages[0])).Text;
            Assert.Contains("日本語", text);
            Assert.Contains("한국어", text);
            // The embedded subset is small (the full CFF table is 15 MB) and draws the characters
            var size = (int)(saved.Trailer.GetInteger(PdfName.Size) ?? 0);
            // (one per language variant: Japanese, Korean)
            var cff = Enumerable.Range(1, size - 1).Select(saved.GetObject).OfType<PdfStream>()
                .Where(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "CIDFontType0C").ToList();
            Assert.Equal(2, cff.Count);
            Assert.All(cff, s => Assert.InRange(saved.GetStreamData(s).Length, 1, 200_000));
            var drawn = PageInterpreter.Build(saved, saved.Pages[0]);
            Assert.Contains(drawn.Text, t => t.Text == "日");
        }
        finally
        {
            CjkFonts.FolderForTests = null;
        }
    }

    /// <summary>
    /// A non-embedded CIDFontType0 of the collection Adobe-KR (ISO 32000-2, 9.7.5.2: a reader supports Adobe-KR-9), once with Identity-H and
    /// the CIDs of Adobe-KR („한“ 0x0B13, „어“ 0x075F), once with the CMap UniAKR-UTF16-H: the text comes from Adobe-KR-UCS2, the glyphs
    /// from the Korean Noto font. Before: no substitute, no text, nothing drawn (docs/FEHLERGEDAECHTNIS.md, No. 570).
    /// </summary>
    [Theory]
    [InlineData("Identity-H", "<0B13075F>")]
    [InlineData("UniAKR-UTF16-H", "<D55CC5B4>")]
    public void AdobeKrWithoutEmbedding_TextAndGlyphs(string encoding, string codes)
    {
        CjkFonts.FolderForTests = Path.Combine(Repo(), "Schriften", "CJK");
        try
        {
            var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Musterschrift-KR")), ("Flags", PdfInteger.Of(4)),
                ("FontBBox", TestPdf.Box(0, -120, 1000, 880)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(880)),
                ("Descent", PdfInteger.Of(-120)), ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)));
            var cidFont = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType0")), ("BaseFont", TestPdf.N("Musterschrift-KR")),
                ("CIDSystemInfo", TestPdf.Dict(("Registry", new PdfString("Adobe"u8.ToArray())), ("Ordering", new PdfString("KR"u8.ToArray())),
                    ("Supplement", PdfInteger.Of(9)))), ("FontDescriptor", descriptor), ("DW", PdfInteger.Of(1000)));
            var font = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("Musterschrift-KR")),
                ("Encoding", TestPdf.N(encoding)), ("DescendantFonts", new PdfArray([cidFont])));
            var (doc, list) = InterpreterTests.Page($"BT /F1 60 Tf 20 80 Td {codes} Tj ET",
                pdf => TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(pdf.Add(font)))))));
            using (doc)
            {
                Assert.Equal("한어", string.Concat(list.Text.Select(t => t.Text)));
                Assert.False(doc.MissingCjkFonts);
                using var bitmap = PdfKnead.Rendering.SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new PdfKnead.Rendering.ImageCache());
                var ink = 0;
                for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                    if (bitmap.GetPixel(x, y).Red < 128) ink++;
                Assert.True(ink > 300, $"gezeichnet: {ink} Pixel");
            }
        }
        finally
        {
            CjkFonts.FolderForTests = null;
        }
    }
}
