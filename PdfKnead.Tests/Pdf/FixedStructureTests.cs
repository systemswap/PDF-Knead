using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Own elements fixed into the page of a tagged file (docs/ABLAUFPLAN.md, 6.4 step 4; ISO 14289-1, 7.3, 7.13; docs/FEHLERGEDAECHTNIS.md,
/// No. 634): a signature becomes a Figure with /Alt „Unterschrift“ (never the name) and a layout /BBox, a freehand drawing a Figure, a fixed
/// text a P, a shape an artifact; each next to the content it sits on. Before: drawn without marks, untagged content, and the save took the
/// PDF/UA identification for „neuer Inhalt ohne Struktur“.
/// </summary>
public sealed class FixedStructureTests
{
    /// <summary>One page: a paragraph (8, MCID 0) at the top under a Document (7).</summary>
    private static byte[] Tagged() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 6 0 R/MarkInfo<</Marked true>>/Lang(de-DE)>>",
        [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>/StructParents 0>>",
        [4] = ("", "/P <</MCID 0>> BDC BT /F1 12 Tf 72 760 Td (Mit freundlichen Gruessen) Tj ET EMC"),
        [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
        [6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1>>",
        [7] = "<</Type/StructElem/S/Document/P 6 0 R/K 8 0 R>>",
        [8] = "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 0>>",
        [15] = "<</Nums[0[8 0 R]]>>",
    });

    private static (PdfDocument Saved, PdfSaveResult Result) Save(PageElement[] elements, bool flatten = false)
    {
        using var doc = PdfDocument.Open(Tagged());
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, [PageItem.From(doc)[0].WithElements(elements)], output, new PdfSaveOptions { Flatten = flatten });
        return (PdfDocument.Open(output.ToArray()), result);
    }

    /// <summary>The structure elements, depth first.</summary>
    private static List<PdfDictionary> Elements(PdfDocument doc)
    {
        var found = new List<PdfDictionary>();
        var stack = new Stack<PdfObject?>([doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.Get(PdfName.Get("K"))]);
        while (stack.TryPop(out var k))
            switch (k)
            {
                case PdfArray a:
                    for (var i = a.Count - 1; i >= 0; i--) stack.Push(a.Get(i));
                    break;
                case PdfDictionary e when e.ContainsKey(PdfName.Get("S")):
                    found.Add(e);
                    stack.Push(e.Get(PdfName.Get("K")));
                    break;
            }
        return found;
    }

    private static string Content(PdfDocument doc)
    {
        using var content = PageInterpreter.PageContent(doc, doc.Pages[0].Dictionary);
        return Encoding.Latin1.GetString(content.Span);
    }

    /// <summary>
    /// A signature (name in a handwriting font) and a freehand drawing below the paragraph: Figures with /Alt „Unterschrift“ and
    /// „Handschriftliche Zeichnung“ and a layout /BBox, their content marked; the name is nowhere in the tree; no reason given; the tree clean.
    /// </summary>
    [Fact]
    public void SignatureAndInk_FiguresWithoutTheName()
    {
        var signature = new SignatureElement { Rect = new PdfRectangle(72, 680, 272, 730), Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf" };
        var ink = new InkElement { Rect = new PdfRectangle(300, 680, 400, 730), Strokes = [[0f, 0f, 10f, 10f, 20f, 5f]] };
        var (saved, result) = Save([signature, ink]);
        using (saved)
        {
            var figures = Elements(saved).Where(e => e.GetName(PdfName.Get("S"))!.Value == "Figure").ToList();
            Assert.Equal(["Unterschrift", "Handschriftliche Zeichnung"], figures.Select(f => f.GetString(PdfName.Get("Alt"))!.ToText()));
            Assert.All(figures, f => Assert.Equal("Layout", f.GetDictionary(PdfName.Get("A"))!.GetName(PdfName.Get("O"))!.Value));
            Assert.DoesNotContain(Elements(saved), e => e.GetString(PdfName.Get("Alt"))?.ToText().Contains("Mustermann", StringComparison.Ordinal) == true);
            Assert.Contains("/Figure <</MCID 1>> BDC", Content(saved));
            Assert.DoesNotContain("neuer Inhalt ohne Struktur", result.AccessibilityBreaks);
            StructureAssert.Clean(saved);
        }
    }

    /// <summary>Flattened: a text becomes a P, a shape an artifact of the layout.</summary>
    [Fact]
    public void Flattened_TextIsP_ShapeIsArtifact()
    {
        var text = new TextElement { Rect = new PdfRectangle(72, 600, 300, 640), Text = "Erika Mustermann", FontSize = 14 };
        var shape = new ShapeElement { Rect = new PdfRectangle(72, 500, 172, 550), Kind = ShapeKind.Rectangle };
        var (saved, _) = Save([text, shape], flatten: true);
        using (saved)
        {
            Assert.Equal(2, Elements(saved).Count(e => e.GetName(PdfName.Get("S"))!.Value == "P"));
            Assert.Contains("/Artifact <</Type /Layout>> BDC", Content(saved));
            StructureAssert.Clean(saved);
        }
    }
}
