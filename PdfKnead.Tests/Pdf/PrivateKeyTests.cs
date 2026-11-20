using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The program's own keys are second-class names (ISO 32000-2, Annex E.2: four characters, then „_“ — „PDFK_…“; docs/FEHLERGEDAECHTNIS.md,
/// No. 599). Before, they were „PDFKnead…“, which the Arlington checker reports as entries the dictionaries must not have. Files of earlier
/// versions are still read; saved again, the earlier names go.
/// </summary>
public sealed class PrivateKeyTests
{
    private static readonly string[] Earlier = ["PDFKneadElement", "PDFKneadElements", "PDFKneadText", "PDFKneadCode", "PDFKneadStartgroesse", "PDFKneadOcr"];

    /// <summary>Every key of every dictionary in a file (objects, also in object streams; nested dictionaries; stream dictionaries).</summary>
    private static HashSet<string> Keys(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(PdfObject? o, int depth)
        {
            if (o is null || depth > 12) return;
            switch (o)
            {
                case PdfStream st when seen.Add(st):
                    Walk(st.Dictionary, depth + 1);
                    break;
                case PdfDictionary d when seen.Add(d):
                    foreach (var (k, v) in d.Entries)
                    {
                        keys.Add(k.Value);
                        if (v is not PdfReference) Walk(v, depth + 1);
                    }
                    break;
                case PdfArray a when seen.Add(a):
                    for (var i = 0; i < a.Count; i++) if (a[i] is not PdfReference) Walk(a[i], depth + 1);
                    break;
            }
        }
        for (var n = 1; n <= doc.HighestObjectNumber; n++) Walk(doc.GetObject(n), 0);
        return keys;
    }

    /// <summary>A file with an own shape, a dimension and a text field that shrinks to fit: only the new names in it.</summary>
    [Fact]
    public void NewFiles_UseSecondClassNames_Only()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var dimension = ElementAppearance.Dimension(Measuring.Measure(MeasureKind.Length, [(100, 100), (300, 100)], []), 0x1F4E9A, 1.5, 0.2, false, false, 0)!;
        var field = new FieldElement
        {
            Rect = new PdfRectangle(100, 600, 300, 620),
            Spec = new FieldSpec { Name = "Feld", FontSize = 12, ShrinkToFit = true },
        };
        var items = PageItem.From(doc).ToArray();
        items[0] = items[0].WithElements([.. items[0].Elements, new ShapeElement { Rect = new PdfRectangle(50, 50, 150, 150) }, dimension, field]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions());
        var keys = Keys(output.ToArray());
        Assert.Contains("PDFK_Element", keys);
        Assert.Contains("PDFK_Elements", keys);
        Assert.Contains("PDFK_Startgroesse", keys);
        Assert.All(Earlier, name => Assert.DoesNotContain(name, keys));
        // every key not of the standard that the program writes is a second-class name — the prefix alone only as the key of its developer
        // extensions dictionary in /Extensions (7.12.2; No. 793)
        Assert.All(keys.Where(k => k.StartsWith("PDFK", StringComparison.Ordinal) && k != "PDFK"), k => Assert.Matches("^PDFK[_:]", k));
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.Equal(2, ElementStore.For(saved, saved.Pages[0]).Elements.Count(e => e is ShapeElement or DimensionElement));
    }

    /// <summary>A file of an earlier version (earlier names): its own shape is an element again; saved, it carries only the new names.</summary>
    [Fact]
    public void EarlierFiles_StillRead_SavedWithTheNewNames()
    {
        var pdf = TestPdf.Simple(1);
        var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 100, 100))),
            "0 0 1 RG 2 w 1 1 98 98 re S"u8.ToArray()));
        var data = TestPdf.Dict(("Art", TestPdf.N("Form")), ("Formart", TestPdf.N("Rectangle")), ("Rand", PdfInteger.Of(0x1F4E9A)), ("Breite", new PdfReal(2)));
        var annot = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Square")), ("Rect", TestPdf.Box(50, 50, 150, 150)),
            ("AP", TestPdf.Dict(("N", TestPdf.Ref(form)))), (ElementStore.LegacyKey.Value, data)));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(annot)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(ElementStore.LegacyCatalogKey, PdfBoolean.True);
        using var doc = PdfDocument.Open(pdf.Build());
        var shape = Assert.Single(ElementStore.For(doc, doc.Pages[0]).Elements.OfType<ShapeElement>());
        Assert.Equal(0x1F4E9A, shape.StrokeColor);

        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions());
        var keys = Keys(output.ToArray());
        Assert.All(Earlier, name => Assert.DoesNotContain(name, keys));
        Assert.Contains("PDFK_Element", keys);
        Assert.Contains("PDFK_Elements", keys);
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.Single(ElementStore.For(saved, saved.Pages[0]).Elements.OfType<ShapeElement>());
    }

    /// <summary>
    /// ISO 32000-2, 7.12.1 sentence 3 and 7.12.2 sentence 7 (shall): a key of /Extensions is a registered prefix. PDFK is not registered yet
    /// (the user has it registered, 12.11.2026): no declaration for the program's own keys — in 1.7, 2.0 and 1.4 —, and one a file carries
    /// (from a save of No. 793) is taken out, while the declarations of others stay (docs/FEHLERGEDAECHTNIS.md, No. 800). Once the prefix is
    /// registered, <c>PdfWriter.Session.OwnPrefixRegistered</c> turns the declaration of Annex E.2, sentence 17 on, and this test changes with it.
    /// </summary>
    [Theory]
    [InlineData("1.7")]
    [InlineData("2.0")]
    [InlineData("1.4")]
    public void OwnKeys_NotDeclaredWhileThePrefixIsUnregistered(string version)
    {
        var pdf = TestPdf.Simple(1);
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("Extensions"), TestPdf.Dict(
            ("PDFK", TestPdf.Dict(("BaseVersion", TestPdf.N("1.7")), ("ExtensionLevel", PdfInteger.Of(1)))),
            ("ADBE", TestPdf.Dict(("BaseVersion", TestPdf.N("1.7")), ("ExtensionLevel", PdfInteger.Of(3))))));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("Version"), TestPdf.N("1.7"));
        using var doc = PdfDocument.Open(pdf.Build());
        var items = PageItem.From(doc).ToArray();
        items[0] = items[0].WithElements([.. items[0].Elements, new ShapeElement { Rect = new PdfRectangle(50, 50, 150, 150) }]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { Version = version });
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.Contains("PDFK_Element", Keys(output.ToArray()));
        var extensions = saved.Catalog!.GetDictionary(PdfName.Get("Extensions"));
        Assert.True(extensions?.Get(PdfName.Get("PDFK")) is null or PdfNull, $"{version}: {extensions}");
        if (version != "1.4") Assert.NotNull(extensions?.GetDictionary(PdfName.Get("ADBE")));   // the declaration of another stays
    }
}
