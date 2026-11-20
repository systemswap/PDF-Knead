using System.Text;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Redacting tagged files (docs/ABLAUFPLAN.md, 6.3 steps 1–4; docs/FEHLERGEDAECHTNIS.md, No. 617): the bars and the reasons in them are
/// artifacts, marked content the old content leaves open is closed before them, and structure elements of the redacted page that lose their
/// description get a neutral one where screen readers need one (decision H).
/// </summary>
public sealed class TaggedRedactionTests
{
    /// <summary>The bar over „Mustermann“ in „Max Mustermann“ (Helvetica 12 at x 72, y 700).</summary>
    private static readonly PdfRectangle Bar = new(96, 695, 180, 714);

    /// <summary>
    /// A one-page file: a paragraph „Max Mustermann“ (MCID 0) and a second element <paramref name="second"/> (its dictionary entries without
    /// /Type, /P and /Pg; MCID 1 is a blue square), tagged unless <paramref name="tagged"/> is false. Objects from 10 on: <paramref name="extra"/>.
    /// </summary>
    private static byte[] File(string second, string lang = "(de-DE)", bool tagged = true, string? content = null, string rootExtra = "",
        string annots = "", string catalogExtra = "", Dictionary<int, object>? extra = null, string parentTree = "0[8 0 R 9 0 R]")
    {
        var objects = new Dictionary<int, object>
        {
            [1] = $"<</Type/Catalog/Pages 2 0 R/StructTreeRoot 7 0 R{(tagged ? "/MarkInfo<</Marked true>>" : "")}{(lang.Length > 0 ? "/Lang" + lang : "")}{catalogExtra}>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = $"<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>/StructParents 0{annots}>>",
            [4] = ("", content ?? "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET EMC /Figure <</MCID 1>> BDC 0 0 1 rg 300 300 50 50 re f EMC"),
            [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            [6] = $"<</Nums[{parentTree}]>>",
            [7] = $"<</Type/StructTreeRoot/K[8 0 R 9 0 R]/ParentTree 6 0 R{rootExtra}>>",
            [8] = "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 0>>",
            [9] = $"<</Type/StructElem/P 7 0 R/Pg 3 0 R{second}>>",
        };
        foreach (var (n, o) in extra ?? []) objects[n] = o;
        return CraftedFileTests.Build(objects);
    }

    /// <summary>The page's content, all streams decoded and joined.</summary>
    private static string Content(PdfDocument doc, int page = 0)
    {
        var sb = new StringBuilder();
        switch (doc.Pages[page].Dictionary.Get(PdfName.Contents))
        {
            case PdfStream single: sb.Append(Encoding.Latin1.GetString(doc.GetStreamData(single))); break;
            case PdfArray parts:
                for (var i = 0; i < parts.Count; i++)
                    if (parts.Get(i) is PdfStream part) sb.Append(Encoding.Latin1.GetString(doc.GetStreamData(part))).Append('\n');
                break;
        }
        return sb.ToString();
    }

    private static int Count(string text, string word) => System.Text.RegularExpressions.Regex.Matches(text, $@"(?<![A-Za-z]){word}(?![A-Za-z])").Count;

    /// <summary>The second structure element of the saved file (the one after the paragraph).</summary>
    private static PdfDictionary Second(PdfDocument doc)
    {
        var kids = doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.GetArray(PdfName.Get("K"))!;
        return Enumerable.Range(0, kids.Count).Select(i => kids.Get(i) as PdfDictionary).Last(d => d is not null)!;
    }

    private const string Artifact = "/Artifact <</Type /Pagination /Subtype /Redaction>> BDC";

    /// <summary>
    /// A redacted page with a tiling pattern beside the bar whose cell content describes its marked content (/ActualText „Kontonummer
    /// Mustermann“) (docs/FEHLERGEDAECHTNIS.md, No. 696; found by the analysis round after phase 6: the bars looked into a pattern cell only
    /// where they reach it): every description of a redacted page goes (No. 134, 624) — the cell is rewritten without it, the pattern still
    /// paints. On a page without bars it stays.
    /// </summary>
    [Fact]
    public void RedactedPage_PatternCellBesideTheBar_ItsDescriptionGoes()
    {
        const string page = "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET EMC /Pattern cs /P0 scn 300 300 100 100 re f";
        var bytes = File("/S/Figure/K 1/Alt(Ein Muster)", content: page,
            extra: new Dictionary<int, object>
            {
                [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>/Pattern<</P0 10 0 R>>>>/StructParents 0>>",
                [10] = ("/Type/Pattern/PatternType 1/PaintType 1/TilingType 1/BBox[0 0 20 20]/XStep 20/YStep 20/Resources<<>>",
                    "/Span <</ActualText (Kontonummer Mustermann)>> BDC 0 0 1 rg 0 0 10 10 re f EMC"),
            });
        using var doc = PdfDocument.Open(bytes);
        var saved = RedactionTests.WithBars(doc, (0, Bar));
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(saved, ["Kontonummer"], null, out _));
        // the pattern still paints (its square stays)
        using var after = PdfDocument.Open(saved);
        Assert.Contains("/Pattern cs", Content(after));
    }

    /// <summary>
    /// The same with the resources shared by two pages (an object of their own, as many files have it) and the pattern drawn on the redacted
    /// first page only: its description goes there; nothing of it stays in the file (No. 696).
    /// </summary>
    [Fact]
    public void RedactedPage_PatternCellInSharedResources_ItsDescriptionGoes()
    {
        var bytes = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 7 0 R/MarkInfo<</Marked true>>/Lang(de-DE)>>",
            [2] = "<</Type/Pages/Kids[3 0 R 13 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources 12 0 R/StructParents 0>>",
            [4] = ("", "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET EMC /Pattern cs /P0 scn 300 300 100 100 re f"),
            [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            [6] = "<</Nums[0[8 0 R]]>>",
            [7] = "<</Type/StructTreeRoot/K[8 0 R]/ParentTree 6 0 R>>",
            [8] = "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 0>>",
            [10] = ("/Type/Pattern/PatternType 1/PaintType 1/TilingType 1/BBox[0 0 20 20]/XStep 20/YStep 20/Resources<<>>",
                "/Span <</ActualText (Kontonummer Mustermann)>> BDC 0 0 1 rg 0 0 10 10 re f EMC"),
            [12] = "<</Font<</F1 5 0 R>>/Pattern<</P0 10 0 R>>>>",
            [13] = "<</Type/Page/Parent 2 0 R/Contents 14 0 R/Resources 12 0 R>>",
            [14] = ("", "/Artifact BMC BT /F1 12 Tf 72 700 Td (Seite zwei) Tj ET EMC"),
        });
        using var doc = PdfDocument.Open(bytes);
        var saved = RedactionTests.WithBars(doc, (0, Bar));
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(saved, ["Kontonummer"], null, out _));
    }

    /// <summary>
    /// The pattern with the description also lies in the resources of another cell that a bar reaches (both share the resources of the
    /// page): the rewriter takes it out of that cell's copy and notes it as replaced — the page still paints it beside the bar, and its
    /// description goes there all the same (No. 696; before, the note let the original stay).
    /// </summary>
    [Fact]
    public void RedactedPage_PatternCellAlsoNamedInARedactedCell_ItsDescriptionGoes()
    {
        var bytes = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 7 0 R/MarkInfo<</Marked true>>/Lang(de-DE)>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources 12 0 R/StructParents 0>>",
            [4] = ("", "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET EMC /Pattern cs /P1 scn 95 690 200 30 re f " +
                "/Pattern cs /P0 scn 300 300 100 100 re f"),
            [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            [6] = "<</Nums[0[8 0 R]]>>",
            [7] = "<</Type/StructTreeRoot/K[8 0 R]/ParentTree 6 0 R>>",
            [8] = "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 0>>",
            [10] = ("/Type/Pattern/PatternType 1/PaintType 1/TilingType 1/BBox[0 0 20 20]/XStep 20/YStep 20/Resources<<>>",
                "/Span <</ActualText (Kontonummer Mustermann)>> BDC 0 0 1 rg 0 0 10 10 re f EMC"),
            // a cell as large as the page, with the resources of the page, painted over its text; the bar covers half of it
            [11] = ("/Type/Pattern/PatternType 1/PaintType 1/TilingType 1/BBox[0 0 595 842]/XStep 595/YStep 842/Resources 12 0 R",
                "/Artifact BMC BT /F1 12 Tf 100 700 Td (Musterwort Sichtbar) Tj ET EMC"),
            [12] = "<</Font<</F1 5 0 R>>/Pattern<</P0 10 0 R/P1 11 0 R>>>>",
        });
        using var doc = PdfDocument.Open(bytes);
        var saved = RedactionTests.WithBars(doc, (0, Bar));
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(saved, ["Kontonummer", "Musterwort"], null, out _));
    }

    /// <summary>
    /// Resources shared by the pages and by the cell of a pattern hold a form whose text a bar on the first page covers; the second page,
    /// also redacted, paints the cell and a pattern with a description. The form is only named in the cell's resources, never drawn by it:
    /// it is not copied while the description goes — such a copy, with the unredacted text, would stay in the file where the writer leaves
    /// out the original (No. 696).
    /// </summary>
    [Fact]
    public void RedactedPages_FormOnlyNamedInACellsSharedResources_IsNotCopied()
    {
        var bytes = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 7 0 R/MarkInfo<</Marked true>>/Lang(de-DE)>>",
            [2] = "<</Type/Pages/Kids[3 0 R 13 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources 12 0 R/StructParents 0>>",
            [4] = ("", "/P <</MCID 0>> BDC BT /F1 12 Tf 72 600 Td (Max Mustermann) Tj ET EMC /Artifact BMC /X0 Do EMC"),
            [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            [6] = "<</Nums[0[8 0 R]]>>",
            [7] = "<</Type/StructTreeRoot/K[8 0 R]/ParentTree 6 0 R>>",
            [8] = "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 0>>",
            [9] = ("/Type/XObject/Subtype/Form/BBox[0 0 595 842]/Resources 12 0 R", "BT /F1 12 Tf 100 700 Td (Formgeheimnis) Tj ET"),
            [10] = ("/Type/Pattern/PatternType 1/PaintType 1/TilingType 1/BBox[0 0 20 20]/XStep 20/YStep 20/Resources<<>>",
                "/Span <</ActualText (Kontonummer Mustermann)>> BDC 0 0 1 rg 0 0 10 10 re f EMC"),
            [11] = ("/Type/Pattern/PatternType 1/PaintType 1/TilingType 1/BBox[0 0 595 842]/XStep 595/YStep 842/Resources 12 0 R",
                "0 1 0 rg 0 0 5 5 re f"),
            // the cell first: it is looked into before the pattern with the description is done
            [12] = "<</Font<</F1 5 0 R>>/XObject<</X0 9 0 R>>/Pattern<</P1 11 0 R/P0 10 0 R>>>>",
            [13] = "<</Type/Page/Parent 2 0 R/Contents 14 0 R/Resources 12 0 R>>",
            [14] = ("", "/Artifact BMC BT /F1 12 Tf 72 600 Td (Seite zwei) Tj /Pattern cs /P1 scn 0 0 595 842 re f /P0 scn 300 300 100 100 re f ET EMC"),
        });
        using var doc = PdfDocument.Open(bytes);
        var saved = RedactionTests.WithBars(doc, (0, Bar), (1, new PdfRectangle(70, 595, 150, 615)));
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(saved, ["Kontonummer", "Formgeheimnis"], null, out _));
    }

    /// <summary>The description used by the tests of No. 697: a blue square in marked content whose /ActualText names an account.</summary>
    private const string DescribedSquare = "/Span <</ActualText (Kontonummer Mustermann)>> BDC 0 0 1 rg 0 0 10 10 re f EMC";

    /// <summary>
    /// Two pages sharing their resources (object 12): the first with „Max Mustermann“ (redacted, the bar over „Mustermann“) and
    /// <paramref name="first"/>, the second with <paramref name="second"/>; objects from 20 on: <paramref name="extra"/>. Saved with the bar,
    /// the annotations of the pages kept (the bar is added to what the page has). Returns the file.
    /// </summary>
    private static byte[] RedactedWithSharedResources(string first, string second, string resources, Dictionary<int, object> extra, string annots1 = "",
        string annots2 = "")
    {
        var objects = new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 7 0 R/MarkInfo<</Marked true>>/Lang(de-DE)>>",
            [2] = "<</Type/Pages/Kids[3 0 R 13 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [3] = $"<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources 12 0 R/StructParents 0{annots1}>>",
            [4] = ("", "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET EMC /Artifact BMC " + first + " EMC"),
            [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            [6] = "<</Nums[0[8 0 R]]>>",
            [7] = "<</Type/StructTreeRoot/K[8 0 R]/ParentTree 6 0 R>>",
            [8] = "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 0>>",
            [12] = $"<</Font<</F1 5 0 R>>{resources}>>",
            [13] = $"<</Type/Page/Parent 2 0 R/Contents 14 0 R/Resources 12 0 R{annots2}>>",
            [14] = ("", "/Artifact BMC BT /F1 12 Tf 72 600 Td (Seite zwei) Tj ET " + second + " EMC"),
        };
        foreach (var (key, value) in extra) objects[key] = value;
        using var doc = PdfDocument.Open(CraftedFileTests.Build(objects));
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([.. items[0].Elements, new RedactionElement { Rect = Bar }]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true, CompressStreams = false, ObjectStreams = false });
        return output.ToArray();
    }

    /// <summary>
    /// The group of a soft mask beside the bar gives a replacement text in its own content (docs/FEHLERGEDAECHTNIS.md, No. 697; found by the
    /// root-cause matrix of the analysis round: forms were looked into for descriptions, mask groups not): it goes like one in a form.
    /// </summary>
    [Fact]
    public void RedactedPage_SoftMaskGroupBesideTheBar_ItsDescriptionGoes()
    {
        var saved = RedactedWithSharedResources("q /SM gs 0 0 1 rg 300 300 100 100 re f Q", "", "/ExtGState<</SM<</Type/ExtGState/SMask<</S/Luminosity/G 20 0 R>>>>>>",
            new() { [20] = ("/Type/XObject/Subtype/Form/BBox[0 0 595 842]/Group<</S/Transparency/CS/DeviceRGB>>/Resources<<>>", "1 g " + DescribedSquare) });
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(saved, ["Kontonummer"], null, out _));
        using var after = PdfDocument.Open(saved);
        Assert.Matches(@"/\w+ gs", Content(after));   // the mask still applies (a copy of the state, under a name of its own)
    }

    /// <summary>
    /// A form beside the bar that describes nothing itself paints a pattern whose cell does; the pattern lies in resources the second page
    /// shares (named there, not painted): the description goes from the copy and from the file (No. 697).
    /// </summary>
    [Fact]
    public void RedactedPage_PatternInAFormBesideTheBar_ItsDescriptionGoes()
    {
        var saved = RedactedWithSharedResources("/X0 Do", "", "/XObject<</X0 20 0 R>>/Pattern<</P0 21 0 R>>",
            new()
            {
                [20] = ("/Type/XObject/Subtype/Form/BBox[0 0 595 842]/Resources 12 0 R", "/Pattern cs /P0 scn 300 300 100 100 re f"),
                [21] = ("/Type/Pattern/PatternType 1/PaintType 1/TilingType 1/BBox[0 0 20 20]/XStep 20/YStep 20/Resources<<>>", DescribedSquare),
            });
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(saved, ["Kontonummer"], null, out _));
    }

    /// <summary>
    /// The glyph of a Type 3 font shown beside the bar gives a replacement text; the font is shared with the second page, which shows another
    /// glyph of it: the description goes from the redacted page's copy and from the file, the second page keeps its glyph (No. 697).
    /// </summary>
    [Fact]
    public void RedactedPage_Type3GlyphBesideTheBar_ItsDescriptionGoes()
    {
        var saved = RedactedWithSharedResources("BT /T3 12 Tf 300 300 Td (a) Tj ET", "BT /T3 12 Tf 300 300 Td (b) Tj ET", "",
            new()
            {
                [12] = "<</Font<</F1 5 0 R/T3 20 0 R>>>>",
                [20] = "<</Type/Font/Subtype/Type3/FontBBox[0 0 1000 1000]/FontMatrix[0.001 0 0 0.001 0 0]/CharProcs<</a 21 0 R/b 22 0 R>>" +
                       "/Encoding<</Type/Encoding/Differences[97/a/b]>>/FirstChar 97/LastChar 98/Widths[1000 1000]/Resources<<>>>>",
                [21] = ("", "1000 0 d0 " + DescribedSquare),
                [22] = ("", "1000 0 d0 0 0 1 rg 0 0 500 900 re f"),
            });
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(saved, ["Kontonummer"], null, out _));
        using var after = PdfDocument.Open(saved);
        Assert.Contains("0 0 500 900 re", Encoding.Latin1.GetString(saved));   // the second page's glyph stays
    }

    /// <summary>
    /// An annotation beside the bar whose appearance gives a replacement text; the same appearance stream serves an annotation on the second
    /// page: it goes from the annotation of the redacted page, the other keeps it as it is (No. 697).
    /// </summary>
    [Fact]
    public void RedactedPage_AnnotationAppearanceBesideTheBar_ItsDescriptionGoes()
    {
        var saved = RedactedWithSharedResources("", "", "",
            new()
            {
                [20] = "<</Type/Annot/Subtype/Square/Rect[300 300 400 400]/F 4/AP<</N 22 0 R>>>>",
                [21] = "<</Type/Annot/Subtype/Square/Rect[300 300 400 400]/F 4/AP<</N 22 0 R>>>>",
                [22] = ("/Type/XObject/Subtype/Form/BBox[0 0 100 100]/Resources<<>>", DescribedSquare),
            }, annots1: "/Annots[20 0 R]", annots2: "/Annots[21 0 R]");
        // once in the file: the second page's annotation keeps the shared stream
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Encoding.Latin1.GetString(saved), "Kontonummer"));
        using var after = PdfDocument.Open(saved);
        var first = after.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0) as PdfDictionary;
        var appearance = first!.GetDictionary(PdfName.Get("AP"))!.Get(PdfName.N) as PdfStream;
        Assert.NotNull(appearance);
        Assert.DoesNotContain("Kontonummer", Encoding.Latin1.GetString(after.GetStreamData(appearance!)));
        Assert.Contains("re f", Encoding.Latin1.GetString(after.GetStreamData(appearance!)));   // it still draws
    }

    /// <summary>
    /// A redacted page whose elements carry a title (/T, „Max Mustermann“) and a table summary (/Summary in their attributes) (docs/
    /// FEHLERGEDAECHTNIS.md, No. 694; found by the overall check of redaction, 6.9): both are text describing the content as much as /Alt —
    /// they go with the bar like the replacement texts (No. 134). Before they stayed and named what the bar took.
    /// </summary>
    [Fact]
    public void RedactedPage_TitlesAndSummariesGo()
    {
        using var doc = PdfDocument.Open(File("/S/Table/K 1/T(Tabelle zu Mustermann)/A<</O/Table/Summary(Daten von Max Mustermann)>>",
            extra: new Dictionary<int, object> { [8] = "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 0/T(Max Mustermann)>>" }));
        var saved = RedactionTests.WithBars(doc, (0, Bar));
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(saved, ["Mustermann"], null, out _));
        using var after = PdfDocument.Open(saved);
        var table = Second(after);
        Assert.False(table.ContainsKey(PdfName.Get("T")));
        Assert.False(table.GetDictionary(PdfName.Get("A"))!.ContainsKey(PdfName.Get("Summary")));
        Assert.Equal("Table", table.GetDictionary(PdfName.Get("A"))?.GetName(PdfName.Get("O"))?.Value);   // the other attributes stay
    }

    /// <summary>
    /// A bar on a tagged page is an artifact of subtype Redaction, outside all tagged content (before: the bar was untagged content — real
    /// content without structure, ISO 14289-1, 7.1).
    /// </summary>
    [Fact]
    public void TaggedPage_BarIsAnArtifact()
    {
        using var doc = PdfDocument.Open(File("/S/Figure/K 1/Alt(Ein blaues Quadrat)"));
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, Bar)));
        var content = Content(saved);
        var at = content.IndexOf(Artifact, StringComparison.Ordinal);
        Assert.True(at >= 0, content);
        var bar = content[at..];
        Assert.Contains(" re", bar);
        Assert.Matches(@"f\s+Q\s+EMC", bar);
        // nothing marked is open where the artifact begins
        var before = content[..at];
        Assert.Equal(Count(before, "BDC") + Count(before, "BMC"), Count(before, "EMC"));
        Assert.DoesNotContain("Mustermann", RedactionTests.Everything(saved));
    }

    /// <summary>Old content that leaves marked content open: it is closed before the bar, which would otherwise stand inside the paragraph.</summary>
    [Fact]
    public void TaggedPage_OpenMarkedContentClosedBeforeTheBar()
    {
        using var doc = PdfDocument.Open(File("/S/Figure/K 1/Alt(Ein blaues Quadrat)",
            content: "/Figure <</MCID 1>> BDC 0 0 1 rg 300 300 50 50 re f EMC /P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET"));
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, Bar)));
        var content = Content(saved);
        var at = content.IndexOf(Artifact, StringComparison.Ordinal);
        Assert.True(at >= 0, content);
        var before = content[..at];
        Assert.Equal(Count(before, "BDC") + Count(before, "BMC"), Count(before, "EMC"));
        Assert.Equal(Count(content, "BDC") + Count(content, "BMC"), Count(content, "EMC"));
    }

    /// <summary>An untagged file: the bars as before, without marked content (the comparisons of redacted content rely on it).</summary>
    [Fact]
    public void UntaggedPage_BarUnchanged()
    {
        using var doc = PdfDocument.Open(File("/S/Figure/K 1", tagged: false));
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, Bar)));
        var content = Content(saved);
        Assert.DoesNotContain("/Artifact", content);
        Assert.Matches(@"q 0 g\s+[\d. ]+ re\s+f\s+Q\s*$", content);
    }

    /// <summary>
    /// Redacting the same page twice: each save adds its own bars once; the artifacts are not stacked inside each other. (The figure whose
    /// square lies wholly under the first bar goes, its empty marked content becomes an artifact too — 6.6 b, No. 649: counted are the bars.)
    /// </summary>
    [Fact]
    public void TaggedPage_RedactedTwice_ArtifactsSideBySide()
    {
        using var doc = PdfDocument.Open(File("/S/Figure/K 1/Alt(Ein blaues Quadrat)"));
        using var once = PdfDocument.Open(RedactionTests.WithBars(doc, (0, Bar)));
        using var twice = PdfDocument.Open(RedactionTests.WithBars(once, (0, new PdfRectangle(290, 290, 360, 360))));
        var content = Content(twice);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(content, @"/Subtype\s*/Redaction").Count);
        var second = content.LastIndexOf(Artifact, StringComparison.Ordinal);
        var before = content[..second];
        Assert.Equal(Count(before, "BDC") + Count(before, "BMC"), Count(before, "EMC"));
    }

    /// <summary>
    /// The reason written into a bar of a tagged file is an artifact like the bar, with the text as /ActualText; drawn over the page it is no
    /// new content without structure (before: untagged content, and the save counted it as breaking accessibility).
    /// </summary>
    [Fact]
    public void TaggedPage_ReasonInBarIsAnArtifactWithActualText()
    {
        var box = RectD.FromLtrb(0, 0, 595, 842);
        var bars = new[] { new ReasonedBar(0, new PdfRectangle(100, 700, 300, 714), "Art. 6 DSGVO") };
        var tagged = RedactionReasons.Labels(bars, box, 0xFFFFFF, tagged: true)!;
        var plain = RedactionReasons.Labels(bars, box, 0xFFFFFF)!;
        var text = Encoding.ASCII.GetString(tagged.RawData!);
        var hex = "FEFF" + string.Concat("Art. 6 DSGVO".Select(c => ((int)c).ToString("X4")));
        Assert.True(text.Contains("/Artifact <</Type /Pagination /Subtype /Redaction /ActualText <" + hex + ">>> BDC", StringComparison.Ordinal), text[..Math.Min(200, text.Length)]);
        Assert.True(StructureRoles.OnlyArtifacts(tagged.RawData!));
        Assert.False(StructureRoles.OnlyArtifacts(plain.RawData!));
        Assert.DoesNotContain("/Artifact", Encoding.ASCII.GetString(plain.RawData!));

        // saved over a tagged page: not „neuer Inhalt ohne Struktur“; the untagged form still is
        using var doc = PdfDocument.Open(File("/S/Figure/K 1/Alt(Ein blaues Quadrat)"));
        foreach (var (form, breaks) in new[] { (tagged, false), (plain, true) })
        {
            using var output = new MemoryStream();
            var result = PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Overlays = new Dictionary<int, IReadOnlyList<PdfStream>> { [0] = [form] } });
            Assert.Equal(breaks, result.AccessibilityBreaks.Contains("neuer Inhalt ohne Struktur"));
        }
    }

    /// <summary>
    /// An inline image paints (ISO 32000-2, 8.9.7): a form drawn over a tagged page with an inline image outside an artifact is new content
    /// without structure, inside one it is none (docs/FEHLERGEDAECHTNIS.md, No. 629; before: the parser returns an inline image as „BI“, the
    /// list of painting operators knew only „EI“ — the form counted as all artifact and the PDF/UA identification stayed).
    /// </summary>
    [Fact]
    public void OverlayWithInlineImage_IsNewContentUnlessArtifact()
    {
        const string image = "q 10 0 0 10 100 100 cm BI /W 1 /H 1 /CS /G /BPC 8 ID \u0080 EI Q";
        static PdfStream Form(string content)
        {
            var dict = new PdfDictionary();
            dict.Set(PdfName.Type, PdfName.Get("XObject"));
            dict.Set(PdfName.Subtype, PdfName.Get("Form"));
            dict.Set(PdfName.Get("BBox"), new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(595), PdfInteger.Of(842)]));
            return new PdfStream(dict, Encoding.Latin1.GetBytes(content));
        }
        var plain = Form(image);
        var artifact = Form("/Artifact BMC " + image + " EMC");
        Assert.False(StructureRoles.OnlyArtifacts(plain.RawData!));
        Assert.True(StructureRoles.OnlyArtifacts(artifact.RawData!));
        using var doc = PdfDocument.Open(File("/S/Figure/K 1/Alt(Ein blaues Quadrat)"));
        foreach (var (form, breaks) in new[] { (plain, true), (artifact, false) })
        {
            using var output = new MemoryStream();
            var result = PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Overlays = new Dictionary<int, IReadOnlyList<PdfStream>> { [0] = [form] } });
            Assert.Equal(breaks, result.AccessibilityBreaks.Contains("neuer Inhalt ohne Struktur"));
        }
    }

    /// <summary>The whole way of the reasons (<see cref="RedactionReasons.Apply"/>): in a tagged file the reason in the bar is an artifact with /ActualText.</summary>
    [Fact]
    public void RedactionReasons_TaggedFile_ReasonIsAnArtifact()
    {
        using var doc = PdfDocument.Open(File("/S/Figure/K 1/Alt(Ein blaues Quadrat)"));
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([new RedactionElement { Rect = new PdfRectangle(70, 690, 300, 716), Reason = "Art. 6 DSGVO" }]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true });
        using var applied = PdfDocument.Open(output.ToArray());
        var done = RedactionReasons.Apply(applied, [.. items], new RedactionReasonSettings(null, true, false, RedactionProtocolPlace.None, null), "x.pdf",
            new PdfSaveOptions(), null);
        Assert.NotNull(done);
        using var withReasons = done.Value.Item1;
        var page = withReasons.Pages[0];
        var forms = page.Resources!.GetDictionary(PdfName.Get("XObject"))!;
        var all = string.Concat(forms.Entries.Select(e => forms.GetStream(e.Key)).OfType<PdfStream>().Select(s => Encoding.Latin1.GetString(withReasons.GetStreamData(s))));
        Assert.Contains("/Subtype /Redaction /ActualText <FEFF", all);
    }

    /// <summary>
    /// The protocol of the reasons in a tagged file (docs/ABLAUFPLAN.md, 6.6 i, item 3; docs/FEHLERGEDAECHTNIS.md, No. 658): its pages are
    /// tagged — a Sect directly in the top Document (no Part or DocumentFragment: it comes from no other document) with H1, paragraphs and a
    /// Table whose head cells have /Scope /Column; nothing on its page untagged, no break named for other pages. Before: the protocol pages
    /// stood untagged, and the save named „Seiten aus einer anderen Datei“.
    /// </summary>
    [Fact]
    public void Protocol_TaggedTarget_PagesAreTagged()
    {
        using var doc = PdfDocument.Open(File("/S/Figure/K 1/Alt(Ein blaues Quadrat)"));
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([new RedactionElement { Rect = new PdfRectangle(70, 690, 300, 716), Reason = "Art. 6 DSGVO" }]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true });
        using var applied = PdfDocument.Open(output.ToArray());
        var done = RedactionReasons.Apply(applied, [.. items], new RedactionReasonSettings(null, false, false, RedactionProtocolPlace.Last, null), "x.pdf",
            new PdfSaveOptions(), null);
        Assert.NotNull(done);
        using var withProtocol = done.Value.Item1;
        Assert.Equal(2, withProtocol.PageCount);
        var root = withProtocol.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!;
        var top = root.Get(PdfName.Get("K")) switch
        {
            PdfArray { Count: 1 } one => Assert.IsType<PdfDictionary>(one.Get(0)),
            var single => Assert.IsType<PdfDictionary>(single),
        };
        var kids = Assert.IsType<PdfArray>(top.Get(PdfName.Get("K")));
        var section = Assert.IsType<PdfDictionary>(kids.Get(kids.Count - 1));
        Assert.Equal("Sect", section.GetName(PdfName.Get("S"))!.Value);
        var parts = Assert.IsType<PdfArray>(section.Get(PdfName.Get("K")));
        var types = Enumerable.Range(0, parts.Count).Select(i => ((PdfDictionary)parts.Get(i)).GetName(PdfName.Get("S"))!.Value).ToList();
        Assert.Equal("H1", types[0]);
        Assert.Equal("Table", types[^1]);
        Assert.All(types[1..^1], t => Assert.Equal("P", t));
        var table = (PdfDictionary)parts.Get(parts.Count - 1);
        var firstRow = (PdfDictionary)((PdfArray)table.Get(PdfName.Get("K"))!).Get(0);
        var head = firstRow.Get(PdfName.Get("K")) switch
        {
            PdfArray cells => (PdfDictionary)cells.Get(0),
            var cell => (PdfDictionary)cell!,
        };
        Assert.Equal("TH", head.GetName(PdfName.Get("S"))!.Value);
        Assert.Equal("Column", head.GetDictionary(PdfName.Get("A"))?.GetName(PdfName.Get("Scope"))?.Value);
        var protocolPage = Assert.Single(Core.Pdf.Accessibility.TaggedContent.Analyse(withProtocol).Pages, p => p.Page == 1);
        Assert.Equal(0, protocolPage.Untagged);
        Assert.Equal(0, protocolPage.McidsWithoutElement);
    }

    /// <summary>
    /// A figure on a redacted page whose description could tell what the bar covers: the old /Alt goes, a neutral one in the document's
    /// language takes its place (before: no /Alt — screen readers said „figure“ without anything; ISO 14289-1, 7.3).
    /// </summary>
    [Theory]
    [InlineData("(de-DE)", "Geschwärzter Inhalt", null)]
    [InlineData("(en-US)", "Redacted content", null)]
    [InlineData("(fr)", "Redacted content", "en")]
    [InlineData("", "Redacted content", "en")]
    public void RedactedPage_FigureGetsANeutralAlt(string lang, string expected, string? elementLang)
    {
        using var doc = PdfDocument.Open(File("/S/Figure/K 1/Alt(Foto von Max Mustermann)", lang));
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, Bar)));
        var figure = Second(saved);
        Assert.Equal(expected, figure.GetString(PdfName.Get("Alt"))?.ToText());
        Assert.Equal(elementLang, figure.GetString(PdfName.Get("Lang"))?.ToText());
        Assert.DoesNotContain("Mustermann", RedactionTests.Everything(saved));
    }

    /// <summary>A type mapped to Figure through the role map counts as Figure; the element's own /Lang decides the language of the text.</summary>
    [Fact]
    public void RedactedPage_RoleMappedFigure_OwnLanguage()
    {
        using var doc = PdfDocument.Open(File("/S/Bild/K 1/Lang(de)/Alt(Foto von Max Mustermann)", "(en)", rootExtra: "/RoleMap<</Bild/Figure>>"));
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, Bar)));
        var figure = Second(saved);
        Assert.Equal("Geschwärzter Inhalt", figure.GetString(PdfName.Get("Alt"))?.ToText());
        Assert.Equal("de", figure.GetString(PdfName.Get("Lang"))?.ToText());
    }

    /// <summary>
    /// Elements with objects: a link loses its /Alt and gets none (it would replace the visible text); an annotation with /Contents gets none
    /// (/Alt and /Contents must not differ, ISO 14289-2, 8.9.4.2), one without gets the neutral text; a field with /TU gets none.
    /// </summary>
    [Theory]
    [InlineData("Link", "/Subtype/Link/A<</S/URI/URI(https://example.org)>>", null)]
    [InlineData("Annot", "/Subtype/Text/Contents(Ein Hinweis)", null)]
    [InlineData("Annot", "/Subtype/Text", "Geschwärzter Inhalt")]
    [InlineData("Form", "/Subtype/Widget/FT/Tx/T(Ort)/V(Musterstadt)/DA(/Helv 10 Tf 0 g)/TU(Wohnort)", null)]
    [InlineData("Form", "/Subtype/Widget/FT/Tx/T(Ort)/V(Musterstadt)/DA(/Helv 10 Tf 0 g)", "Geschwärzter Inhalt")]
    public void RedactedPage_ElementsWithObjects(string type, string annotation, string? expected)
    {
        var file = File($"/S/{type}/Alt(Zu Max Mustermann)/K<</Type/OBJR/Obj 10 0 R/Pg 3 0 R>>", annots: "/Annots[10 0 R]",
            catalogExtra: type == "Form" ? "/AcroForm<</Fields[10 0 R]>>" : "",
            content: "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET EMC", parentTree: "0[8 0 R] 1 9 0 R",
            extra: new() { [10] = $"<</Type/Annot{annotation}/Rect[300 300 350 320]/P 3 0 R/StructParent 1>>" });
        using var doc = PdfDocument.Open(file);
        // the bar beside the elements of the page (a field is one; RedactionTests.WithBars would replace them)
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([.. items[0].Elements, new RedactionElement { Rect = Bar }]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true });
        using var saved = PdfDocument.Open(output.ToArray());
        var element = Second(saved);
        Assert.Equal(type, element.GetName(PdfName.Get("S"))!.Value);
        Assert.Equal(expected, element.GetString(PdfName.Get("Alt"))?.ToText());
        Assert.DoesNotContain("Mustermann", RedactionTests.Everything(saved));
    }

    /// <summary>A paragraph keeps no description and gets none (it has its text).</summary>
    [Fact]
    public void RedactedPage_ParagraphGetsNoAlt()
    {
        using var doc = PdfDocument.Open(File("/S/P/K 1/ActualText(Max Mustermann)",
            content: "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET EMC /P <</MCID 1>> BDC BT /F1 12 Tf 72 600 Td (Erika) Tj ET EMC"));
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, Bar)));
        var element = Second(saved);
        Assert.False(element.ContainsKey(PdfName.Get("Alt")));
        Assert.False(element.ContainsKey(PdfName.Get("ActualText")));
    }

    private const string Ua2Xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                                  "<rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\"><pdfuaid:part>{0}</pdfuaid:part><pdfuaid:rev>2024</pdfuaid:rev></rdf:Description>" +
                                  "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    /// <summary>
    /// A formula with its MathML associated on a redacted page of a PDF/UA-2 file: the file goes (privacy first, No. 444), the formula gets the
    /// neutral /Alt, and the save names the reason the identification goes (docs/ABLAUFPLAN.md, 6.3 step 4); a PDF/UA-1 file does not.
    /// </summary>
    [Theory]
    [InlineData(2, "2.0", true)]
    [InlineData(1, "1.7", false)]
    public void RedactedPage_FormulaLosesMathMl_Ua2Reason(int part, string version, bool reason)
    {
        var file = File("/S/Formula/K 1/AF[11 0 R]", catalogExtra: "/Metadata 13 0 R", extra: new()
        {
            [11] = "<</Type/Filespec/F(formel.mml)/UF(formel.mml)/AFRelationship/Supplement/EF<</F 12 0 R>>>>",
            [12] = ("/Type/EmbeddedFile/Subtype/application#2Fmathml+xml", "<math><mi>x</mi></math>"),
            [13] = ("/Type/Metadata/Subtype/XML", string.Format(Ua2Xmp, part)),
        });
        using var doc = PdfDocument.Open(file);
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([new RedactionElement { Rect = Bar }]);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true, Version = version });
        using var saved = PdfDocument.Open(output.ToArray());
        var formula = Second(saved);
        Assert.False(formula.ContainsKey(PdfName.Get("AF")));
        Assert.Equal("Geschwärzter Inhalt", formula.GetString(PdfName.Get("Alt"))?.ToText());
        Assert.Equal(reason, result.AccessibilityBreaks.Contains("Formel ohne MathML nach Schwärzen"));
    }

    /// <summary>
    /// The leak search of the redaction checks: a redacted word met only as a name of the PDF syntax (the key /ActualText of a reason in a bar,
    /// /XObject) is no leak — it made the check report „ActualText“ and „Object“ in tagged test files; any other name still counts.
    /// </summary>
    [Fact]
    public void LeakSearch_SyntaxNamesAreNoLeak()
    {
        var file = File("/S/Figure/K 1/Alt(Ein blaues Quadrat)",
            content: "/Artifact <</Type /Pagination /Subtype /Redaction /ActualText <FEFF0041>>> BDC 0 0 1 rg 300 300 50 50 re f EMC");
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(file, ["ActualText", "Object", "Redaction"], null, out _));
        var named = File("/S/Figure/K 1", catalogExtra: "/Dests<</Kapitel_Mustermann[3 0 R/Fit]>>");
        Assert.NotEmpty(PdfKnead.Cli.LeakSearch.Find(named, ["Mustermann"], null, out _));
        // the same word as text still counts
        Assert.NotEmpty(PdfKnead.Cli.LeakSearch.Find(File("/S/Figure/K 1", content: "BT /F1 12 Tf 72 700 Td (ActualText) Tj ET"), ["ActualText"], null, out _));
    }

    /// <summary>
    /// The leak search with the names of a file (docs/FEHLERGEDAECHTNIS.md, No. 792, 803): „difference“ only in the key /Differences of the empty
    /// Type 3 font that stands in for a font whose text all went is syntax; a word only in the tag /Formula is syntax when the file's names hold
    /// it — as a whole name token. The same word in a string (/Alt) still counts, also with that name in the set: before, the redaction check
    /// excused a word that was part of any name of the content, wherever it stood (the DocLayNet run with „Länge verbergen“ reported 4 such words).
    /// </summary>
    [Fact]
    public void LeakSearch_FileNamesOnlyAsWholeTokens_NeverInAString()
    {
        var emptyType3 = File("/S/Figure/K 1", content: "/Formula <</MCID 0>> BDC EMC",
            catalogExtra: "/PDFK_Probe <</Type /Font /Subtype /Type3 /Encoding <</Type /Encoding /Differences []>> /CharProcs <<>> /FirstChar 0 /LastChar 0 /Widths [0]>>");
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(emptyType3, ["difference", "DIFFERENCES"], null, out _));
        var names = new HashSet<string>(StringComparer.Ordinal) { "Formula" };
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(emptyType3, ["formula"], null, out _, names));
        Assert.NotEmpty(PdfKnead.Cli.LeakSearch.Find(emptyType3, ["formula"], null, out _));   // without the file's names: counted
        // part of a longer name is no whole token: counted
        Assert.NotEmpty(PdfKnead.Cli.LeakSearch.Find(emptyType3, ["formula"], null, out _, new HashSet<string>(StringComparer.Ordinal) { "Formul" }));
        // the word in /Alt: counted, whatever the names
        var inAlt = File("/S/Formula/K 0/Alt(formula of Max Mustermann)", content: "/Formula <</MCID 0>> BDC EMC");
        Assert.NotEmpty(PdfKnead.Cli.LeakSearch.Find(inAlt, ["formula"], null, out _, names));
    }

    /// <summary>
    /// Two pages: „Max Mustermann“ on the first (MCID 0, span 8), „Erika Mustermann“ on the second (MCID 0, span 9); element 10 is
    /// <paramref name="element"/> (its entries after /Type). The first page is the one redacted in the tests.
    /// </summary>
    private static byte[] TwoPages(string element, string rootKids = "[10 0 R]", string parentOf8 = "10 0 R", string parentOf9 = "10 0 R",
        string parentTree = "0[8 0 R]1[9 0 R]") => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 7 0 R/MarkInfo<</Marked true>>/Lang(de-DE)>>",
        [2] = "<</Type/Pages/Kids[3 0 R 11 0 R]/Count 2/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>/StructParents 0>>",
        [4] = ("", "/Span <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET EMC"),
        [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
        [6] = $"<</Nums[{parentTree}]>>",
        [7] = $"<</Type/StructTreeRoot/K{rootKids}/ParentTree 6 0 R>>",
        [8] = $"<</Type/StructElem/S/Span/P {parentOf8}/Pg 3 0 R/K 0>>",
        [9] = $"<</Type/StructElem/S/Span/P {parentOf9}/Pg 11 0 R/K 0>>",
        [10] = $"<</Type/StructElem{element}>>",
        [11] = "<</Type/Page/Parent 2 0 R/Contents 12 0 R/Resources<</Font<</F1 5 0 R>>>>/StructParents 1>>",
        [12] = ("", "/Span <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Erika Mustermann) Tj ET EMC"),
    });

    /// <summary>
    /// A structure element whose content lies on the redacted page only through an element below it, or through a content reference while
    /// the element names another page, loses its replacement text — it can tell what the bar covers (before: it stayed, because only the
    /// element's own page, its direct references and its ancestors were looked at; docs/FEHLERGEDAECHTNIS.md, No. 618).
    /// </summary>
    [Theory]
    [InlineData("/S/P/P 7 0 R/K[8 0 R]/ActualText(Max Probewort)", "0[8 0 R]1[9 0 R]")]                      // no page of its own, nor its parents
    [InlineData("/S/P/P 7 0 R/Pg 11 0 R/K[9 0 R 8 0 R]/ActualText(Max Probewort)", "0[8 0 R]1[9 0 R]")]      // names the other page
    [InlineData("/S/P/P 7 0 R/Pg 11 0 R/K[0<</Type/MCR/Pg 3 0 R/MCID 0>>]/Alt(Max Probewort)", "0[10 0 R]1[10 0 R]")]   // reference to the redacted page
    public void RedactedPage_DescriptionReachingThePageThroughKidsGoes(string element, string parentTree)
    {
        using var doc = PdfDocument.Open(TwoPages(element, parentTree: parentTree));
        Assert.Contains("Probewort", RedactionTests.Everything(doc));
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, Bar)));
        var everything = RedactionTests.Everything(saved);
        Assert.DoesNotContain("Probewort", everything);
        Assert.Contains("Erika", everything);
    }

    /// <summary>An element on the other page only keeps its replacement text: a bar takes the descriptions of its own page, no more.</summary>
    [Fact]
    public void RedactedPage_DescriptionOfAnotherPageStays()
    {
        using var doc = PdfDocument.Open(TwoPages("/S/P/P 7 0 R/Pg 11 0 R/K[9 0 R]/ActualText(Erika Probewort)", rootKids: "[8 0 R 10 0 R]",
            parentOf8: "7 0 R"));
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, Bar)));
        Assert.Contains("Erika Probewort", RedactionTests.Everything(saved));
    }

    /// <summary>
    /// A ToUnicode map that gives the code of „A“ the first character of the private use area (U+E000) — as fonts of symbols do — the other
    /// codes none of their own (object 14).
    /// </summary>
    private const string PrivateUseMap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /PUA def 1 begincodespacerange <00> <FF> endcodespacerange " +
                                         "1 beginbfchar <41> <E000> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end";

    /// <summary>The font of the fixtures with that map (object 5 replaced).</summary>
    private static Dictionary<int, object> PrivateUseFont(Dictionary<int, object>? more = null)
    {
        var objects = new Dictionary<int, object>
        {
            [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding/ToUnicode 14 0 R>>",
            [14] = ("", PrivateUseMap),
        };
        foreach (var (n, o) in more ?? []) objects[n] = o;
        return objects;
    }

    /// <summary>
    /// A form beside the bar whose content describes marked content — /ActualText in place, and named properties with an associated file
    /// („/AF /MC0 BDC“): on the redacted page both go, as every description of it (docs/ABLAUFPLAN.md, 6.3 step 5; No. 134, 624); before, the
    /// form was taken over as it was, with its descriptions. The same form drawn on a page without a bar keeps them.
    /// </summary>
    [Fact]
    public void RedactedPage_FormBesideTheBar_ItsDescriptionsGo()
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 7 0 R/MarkInfo<</Marked true>>/Lang(de-DE)>>",
            [2] = "<</Type/Pages/Kids[3 0 R 11 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>/XObject<</X1 9 0 R>>>>/StructParents 0>>",
            [4] = ("", "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET EMC /X1 Do"),
            [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            [6] = "<</Nums[0[8 0 R]]>>",
            [7] = "<</Type/StructTreeRoot/K[8 0 R]/ParentTree 6 0 R>>",
            [8] = "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 0>>",
            [9] = ("/Type/XObject/Subtype/Form/BBox[0 0 595 842]/Resources<</Font<</F1 5 0 R>>/Properties<</MC0[10 0 R]>>>>",
                "/Span <</ActualText(Probewort Erika)>> BDC BT /F1 12 Tf 72 300 Td (Text) Tj ET EMC /AF /MC0 BDC 0 0 1 rg 300 300 20 20 re f EMC"),
            [10] = "<</Type/Filespec/F(daten.csv)/UF(daten.csv)/AFRelationship/Data>>",
            [11] = "<</Type/Page/Parent 2 0 R/Contents 12 0 R/Resources<</Font<</F1 5 0 R>>/XObject<</X1 9 0 R>>>>>>",
            [12] = ("", "/X1 Do"),
        });
        using var doc = PdfDocument.Open(file);
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, Bar)));
        string FormsOf(int page)
        {
            var text = new StringBuilder();
            var xobjects = saved.Pages[page].Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("XObject"))!;
            foreach (var (key, _) in xobjects.Entries)
                if (xobjects.Get(key) is PdfStream form)
                {
                    text.Append(Encoding.Latin1.GetString(saved.GetStreamData(form)));
                    if (form.Dictionary.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("Properties"))?.Get(PdfName.Get("MC0")) is PdfArray files)
                        text.Append($" [AF {files.Count}]");
                }
            return text.ToString();
        }
        var redacted = FormsOf(0);
        Assert.DoesNotContain("Probewort", redacted);
        Assert.Contains("[AF 0]", redacted);   // an empty array: it associates none, the pair stays valid
        Assert.Contains("(Text) Tj", redacted);   // what the form draws stays
        var other = FormsOf(1);
        Assert.Contains("Probewort Erika", other);
        Assert.Contains("[AF 1]", other);
    }

    /// <summary>
    /// A form with a description beside the bar, drawn 200 times on the redacted page: one copy without the description, not 200 (No. 164);
    /// drawn where it was.
    /// </summary>
    [Fact]
    public void RedactedPage_FormDrawnOften_OneCopy()
    {
        var draws = new StringBuilder("/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET EMC ");
        for (var i = 0; i < 200; i++) draws.Append($"q 1 0 0 1 {i % 20 * 25} {i / 20 * 25} cm /X1 Do Q ");
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 7 0 R/MarkInfo<</Marked true>>/Lang(de-DE)>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>/XObject<</X1 10 0 R>>>>/StructParents 0>>",
            [4] = ("", draws.ToString()),
            [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            [6] = "<</Nums[0[8 0 R]]>>",
            [7] = "<</Type/StructTreeRoot/K[8 0 R]/ParentTree 6 0 R>>",
            [8] = "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 0>>",
            [10] = ("/Type/XObject/Subtype/Form/BBox[0 0 20 20]", "/Span <</ActualText(Probewort)>> BDC 0 0 1 rg 0 0 10 10 re f EMC"),
        });
        using var doc = PdfDocument.Open(file);
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, Bar)));
        Assert.DoesNotContain("Probewort", RedactionTests.Everything(saved));
        var xobjects = saved.Pages[0].Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("XObject"))!;
        Assert.Single(xobjects.Entries);
        Assert.Equal(200, Count(Content(saved), "Do"));
    }

    /// <summary>
    /// A character of the private use area beside the bar, explained by an /ActualText: the bar takes the replacement text (No. 134, decision
    /// of the user) — the character stays without one. A PDF/UA-2 file names it as the reason its identification goes (ISO 14289-2, 8.4.3);
    /// a PDF/UA-1 file keeps it (ISO 14289-1, 7.2 allows such characters). In an artifact the character is no real content: no reason.
    /// </summary>
    [Theory]
    [InlineData(2, "2.0", "/Span <</MCID 1 /ActualText (Telefon)>> BDC", true)]
    [InlineData(1, "1.7", "/Span <</MCID 1 /ActualText (Telefon)>> BDC", false)]
    [InlineData(2, "2.0", "/Artifact BMC", false)]
    public void RedactedPage_PrivateUseLeftWithoutReplacement_Ua2Reason(int part, string version, string mark, bool reason)
    {
        var file = File("/S/Span/K 1", catalogExtra: "/Metadata 13 0 R",
            content: $"/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET EMC {mark} BT /F1 12 Tf 300 300 Td (A) Tj ET EMC",
            extra: PrivateUseFont(new() { [13] = ("/Type/Metadata/Subtype/XML", string.Format(Ua2Xmp, part)) }));
        using var doc = PdfDocument.Open(file);
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([new RedactionElement { Rect = Bar }]);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true, Version = version });
        Assert.Equal(reason, result.AccessibilityBreaks.Contains("Zeichen aus dem privaten Unicode-Bereich ohne Ersatztext nach Schwärzen"));
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.DoesNotContain("Telefon", RedactionTests.Everything(saved));
    }

    /// <summary>
    /// The detector by itself (shared with the check „barrierefrei?“): a character of the private use area outside any replacement text is
    /// one; inside /ActualText or /Alt, in an artifact, or with the structure element giving one (its MCID) it is not; in a form drawn inside
    /// a marked content with /ActualText it is explained; drawn bare it is not; the font set before a form goes into it.
    /// </summary>
    [Theory]
    [InlineData("BT /F1 12 Tf (A) Tj ET", true)]
    [InlineData("BT /F1 12 Tf (B) Tj ET", false)]
    [InlineData("/Span <</ActualText (Telefon)>> BDC BT /F1 12 Tf (A) Tj ET EMC", false)]
    [InlineData("/Figure <</Alt (Telefon)>> BDC BT /F1 12 Tf [(B) -20 (A)] TJ ET EMC", false)]
    [InlineData("/Artifact BMC BT /F1 12 Tf (A) Tj ET EMC", false)]
    [InlineData("/Span <</MCID 3>> BDC BT /F1 12 Tf (A) Tj ET EMC", false)]
    [InlineData("/Span <</MCID 4>> BDC BT /F1 12 Tf (A) Tj ET EMC", true)]
    [InlineData("/Span <</ActualText (Telefon)>> BDC /F1 12 Tf /X1 Do EMC", false)]
    [InlineData("BT /F1 12 Tf ET /X1 Do", true)]
    [InlineData("/Span <</ActualText (x)>> BDC EMC BT /F1 12 Tf (A) Tj ET", true)]
    public void PrivateUseText_FindsWhatHasNoReplacement(string content, bool expected)
    {
        var file = File("/S/Span/K 1", content: content, extra: PrivateUseFont(new()
        {
            [10] = ("/Type/XObject/Subtype/Form/BBox[0 0 100 100]", "BT 10 10 Td (A) Tj ET"),
        }));
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("Font"), page.Resources!.GetDictionary(PdfName.Get("Font"))!);
        var forms = new PdfDictionary();
        forms.Set(PdfName.Get("X1"), doc.GetObject(10)!);
        resources.Set(PdfName.Get("XObject"), forms);
        Assert.Equal(expected, PrivateUseText.ShownWithoutReplacement(doc, Encoding.Latin1.GetBytes(content), resources, mcid => mcid == 3));
    }

    /// <summary>The standard type through /RoleMap and through /RoleMapNS of a namespace (also onto a type of another namespace).</summary>
    [Fact]
    public void StructureRoles_Resolve()
    {
        var root = new PdfDictionary();
        root.Set(PdfName.Get("RoleMap"), Dict(("Bild", PdfName.Get("Figure"))));
        Assert.Equal("Figure", StructureRoles.Resolve(Dict(("S", PdfName.Get("Bild"))), root));
        Assert.Equal("P", StructureRoles.Resolve(Dict(("S", PdfName.Get("P"))), root));
        var standard = Dict(("NS", new PdfString("http://iso.org/pdf2/ssn"u8.ToArray())));
        var own = Dict(("NS", new PdfString("urn:eigen"u8.ToArray())), ("RoleMapNS", Dict(("Gleichung", new PdfArray([PdfName.Get("Formula"), standard])))));
        Assert.Equal("Formula", StructureRoles.Resolve(Dict(("S", PdfName.Get("Gleichung")), ("NS", own)), root));
        // a cycle ends — without a standard type on it the element stands for none (ISO 32000-2, 14.7.3, note 2; docs/FEHLERGEDAECHTNIS.md,
        // No. 628: before, the chain stopped after 20 steps at whichever type it had reached)
        root.Set(PdfName.Get("RoleMap"), Dict(("A", PdfName.Get("B")), ("B", PdfName.Get("A"))));
        Assert.Null(StructureRoles.Resolve(Dict(("S", PdfName.Get("A"))), root));

        static PdfDictionary Dict(params (string Key, PdfObject Value)[] entries)
        {
            var d = new PdfDictionary();
            foreach (var (k, v) in entries) d.Set(PdfName.Get(k), v);
            return d;
        }
    }
}
