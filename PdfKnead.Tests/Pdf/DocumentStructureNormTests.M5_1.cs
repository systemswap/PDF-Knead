using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Document structure, 7.7 to 7.9: catalogue, page tree, name dictionary, content streams, common data structures, sentence by sentence.</summary>
public sealed partial class DocumentStructureNormTests
{
    /// <summary>Pages and drawings built for the tests of 7.7 to 7.9 below.</summary>
    private static class StructurePages
    {
        /// <summary>A one-page file (200 × 200) with the given content and page resources.</summary>
        internal static byte[] WithContent(string content, string resources = "<< /Font << /F1 5 0 R >> >>") => FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources {resources} /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        /// <summary>
        /// What the display draws of the first page, as text: per entry its kind, the verbs and points of its path and its matrix, then every
        /// glyph with its text and corner. Colours are left out (a PDF/A copy may change how a colour is given, not what is drawn).
        /// </summary>
        internal static string Drawn(PdfDocument doc)
        {
            var list = PageInterpreter.Build(doc, doc.Pages[0]);
            var text = new StringBuilder();
            static string F(double v) => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);
            foreach (var item in list.Items.Span)
            {
                text.Append(item.Kind).Append(item.EvenOdd ? "*" : "").Append(item.Glyph ? "g" : "").Append(':');
                if (item.Path >= 0)
                {
                    var (verb, count, point) = list.Paths[item.Path];
                    for (var v = verb; v < verb + count; v++)
                    {
                        var points = list.Verbs[v] switch { PathVerb.Move or PathVerb.Line => 1, PathVerb.Cubic => 3, _ => 0 };
                        text.Append(list.Verbs[v]);
                        for (var p = 0; p < points; p++, point += 2) text.Append(' ').Append(F(list.Points[point])).Append(',').Append(F(list.Points[point + 1]));
                        text.Append(';');
                    }
                }
                var m = list.Matrices[item.Matrix];
                text.Append(" @").Append(string.Join(",", new[] { m.A, m.B, m.C, m.D, m.E, m.F }.Select(F))).Append('\n');
            }
            foreach (var glyph in list.Text) text.Append('[').Append(glyph.Text).Append("] ").Append(F(glyph.X0)).Append(',').Append(F(glyph.Y0)).Append('\n');
            return text.ToString();
        }

        /// <summary>What the display draws of the first page of a one-page file with the given content.</summary>
        internal static string Drawn(string content)
        {
            using var doc = PdfDocument.Open(WithContent(content));
            return Drawn(doc);
        }

        /// <summary>
        /// A file whose name dictionary holds under <paramref name="key"/> a foreign tree of one node that breaks the order of 7.9.6: keys
        /// „zeta“, „alpha“, „mitte“ with dictionaries /Wert 1, 2, 3.
        /// </summary>
        internal static byte[] WithForeignTree(string key) => FileOf(
            $"<< /Type /Catalog /Pages 2 0 R /Names << /{key} 4 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Names [(zeta) << /Wert 1 >> (alpha) << /Wert 2 >> (mitte) << /Wert 3 >>] >>");

        /// <summary>
        /// The tree under <paramref name="key"/> of the saved file is a proper name tree (7.9.6): keys in byte order, every pair kept with its
        /// value, the root without /Limits, each leaf with the right /Limits, kids and values indirect.
        /// </summary>
        internal static void AssertWrittenInOrder(string key)
        {
            using var saved = Saved(WithForeignTree(key));
            var tree = saved.Catalog!.GetDictionary(PdfName.Get("Names"))!.GetDictionary(PdfName.Get(key));
            Assert.NotNull(tree);
            Assert.False(tree.ContainsKey(PdfName.Get("Limits")));
            var pairs = NameTree.Pairs(tree).Select(p => (Encoding.ASCII.GetString(p.Key),
                (p.Value is PdfReference r ? saved.Resolve(r.Id) : p.Value)!.AsDictionary()!.GetInteger(PdfName.Get("Wert")))).ToList();
            Assert.Equal([("alpha", 2L), ("mitte", 3L), ("zeta", 1L)], pairs);
            Assert.All(RawValues(saved, tree, "Names"), v => Assert.IsType<PdfReference>(v));
            var leaves = new Stack<PdfDictionary>([tree]);
            while (leaves.TryPop(out var node))
            {
                if (node.GetArray(PdfName.Get("Kids")) is { } kids)
                {
                    for (var i = 0; i < kids.Count; i++) leaves.Push(kids.Get(i)!.AsDictionary()!);
                    continue;
                }
                var names = node.GetArray(PdfName.Get("Names"))!;
                if (node == tree) continue;
                var limits = node.GetArray(PdfName.Get("Limits"))!;
                Assert.Equal(((PdfString)names.Get(0)!).Bytes, ((PdfString)limits.Get(0)!).Bytes);
                Assert.Equal(((PdfString)names.Get(names.Count - 2)!).Bytes, ((PdfString)limits.Get(1)!).Bytes);
            }
        }
    }

    /// <summary>
    /// 7.7.2, Table 29, /Version, sentence 14: an update may raise the version of a file through the catalogue of its last section — a file
    /// with header 1.4 whose appended update brings a new catalogue with /Version /1.7 is a file of 1.7; the header stays 1.4, and the
    /// original part read alone is 1.4.
    /// </summary>
    [Fact]
    public void Catalogue_VersionOfTheUpdatesCatalogue_RaisesTheVersion()
    {
        var original = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(
            FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>")).Replace("%PDF-1.7", "%PDF-1.4"));
        using (var before = PdfDocument.Open(original)) Assert.Equal("1.4", before.Version);
        var updated = TestPdf.AppendUpdate(original, 3, 3, null,
            (3, TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(2)), ("Version", TestPdf.N("1.7")))));
        using var doc = PdfDocument.Open(updated);
        Assert.False(doc.WasRepaired);
        Assert.Equal("1.4", doc.HeaderVersion);
        Assert.Equal("1.7", doc.Version);
    }

    /// <summary>
    /// 7.7.3.3, Table 31, /BleedBox, /TrimBox, /ArtBox (sentences 36, 39, 42: default the crop box): PDF-Knead shows and prints a page after its
    /// crop box and does not read the other boxes (14.11.2, M11) — the page is as wide and high as the crop box, and the three boxes stand in
    /// the copy as they stood in the file.
    /// </summary>
    [Fact]
    public void Pages_BleedTrimAndArtBox_KeptAsWritten_SizeAfterTheCropBox()
    {
        var file = FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /CropBox [10 10 210 310] /BleedBox [5 5 215 315] /TrimBox [20 20 200 300] /ArtBox [30 30 190 290] >>");
        using var doc = PdfDocument.Open(file);
        Assert.Equal(200, doc.Pages[0].Width);
        Assert.Equal(300, doc.Pages[0].Height);
        using var saved = Saved(file);
        Assert.Equal(200, saved.Pages[0].Width);
        Assert.Equal(300, saved.Pages[0].Height);
        foreach (var (key, box) in new[] { ("BleedBox", new PdfRectangle(5, 5, 215, 315)), ("TrimBox", new PdfRectangle(20, 20, 200, 300)), ("ArtBox", new PdfRectangle(30, 30, 190, 290)) })
            Assert.Equal(box, PdfRectangle.From(saved.Pages[0].Dictionary.Get(PdfName.Get(key))));
    }

    /// <summary>
    /// 7.7.3.3, Table 31, /Contents, sentence 52: how the content is divided into streams need not be kept — the PDF/A copy writes the content
    /// of a page whose /Contents is an array of two streams as one stream, and it draws the same.
    /// </summary>
    [Fact]
    public void PdfA_ContentsOfTwoStreams_WrittenAsOne_DrawsTheSame()
    {
        const string first = "0 0 1 rg 10 10 50 50 re f 0 0 m";
        const string second = "100 100 l S 1 0 0 rg 120 20 40 60 re f";
        var file = FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents [4 0 R 5 0 R] >>",
            $"<< /Length {first.Length} >>\nstream\n{first}\nendstream", $"<< /Length {second.Length} >>\nstream\n{second}\nendstream");
        using var doc = PdfDocument.Open(file);
        var result = PdfATests.Write(file, PdfAConformance.A2b);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var copy = PdfDocument.Open(result.Bytes);
        Assert.IsType<PdfStream>(copy.Pages[0].Dictionary.Get(PdfName.Contents));
        Assert.Equal(StructurePages.Drawn(doc), StructurePages.Drawn(copy));
    }

    /// <summary>
    /// 7.7.3.3, Table 31, /AA, sentence 79: the additional actions of a page are not inheritable — /AA on a /Pages node goes to no page, as
    /// read and as written; a page's own /AA stays with it.
    /// </summary>
    [Fact]
    public void Pages_AdditionalActionsOfANode_NotInherited()
    {
        var file = FileOf("<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /AA << /O << /S /Named /N /FirstPage >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /AA << /C << /S /Named /N /LastPage >> >> >>");
        using (var doc = PdfDocument.Open(file)) Assert.Null(doc.Pages[0].Dictionary.GetRaw(PdfName.Get("AA")));
        using var saved = Saved(file);
        Assert.Null(saved.Pages[0].Dictionary.GetRaw(PdfName.Get("AA")));
        var own = saved.Pages[1].Dictionary.GetDictionary(PdfName.Get("AA"));
        Assert.NotNull(own);
        Assert.False(own.ContainsKey(PdfName.Get("O")));
        Assert.Equal("LastPage", own.GetDictionary(PdfName.Get("C"))!.GetName(PdfName.N)!.Value);
    }

    /// <summary>
    /// 7.7.4, Table 32, /AP, /JavaScript, /Pages, /Templates (sentences 10 to 26): each a name tree — a foreign one that breaks the order of
    /// 7.9.6 is written as a proper tree, every pair kept (what the entries mean: M9, M10).
    /// </summary>
    [Theory]
    [InlineData("AP")]
    [InlineData("JavaScript")]
    [InlineData("Pages")]
    [InlineData("Templates")]
    public void NameDictionary_AppearanceScriptPageAndTemplateTrees_WrittenInOrder(string key) => StructurePages.AssertWrittenInOrder(key);

    /// <summary>
    /// 7.7.4, Table 32, /IDS, /URLS, /AlternatePresentations, /Renditions (sentences 27 to 49): each a name tree — a foreign one that breaks
    /// the order of 7.9.6 is written as a proper tree, every pair kept (what the entries mean: M10, M11).
    /// </summary>
    [Theory]
    [InlineData("IDS")]
    [InlineData("URLS")]
    [InlineData("AlternatePresentations")]
    [InlineData("Renditions")]
    public void NameDictionary_WebCaptureAlternateAndRenditionTrees_WrittenInOrder(string key) => StructurePages.AssertWrittenInOrder(key);

    /// <summary>
    /// 7.8.2, sentence 28: the operators of marked content (BMC, BDC, EMC) change nothing of what is drawn — a page with a BDC section (with a
    /// property list) and a BMC section draws exactly what the same page without them draws.
    /// </summary>
    [Fact]
    public void MarkedContent_ChangesNothingDrawn()
    {
        var marked = StructurePages.Drawn("/P <</MCID 0>> BDC 0 0 1 rg 10 10 30 30 re f EMC /Span BMC BT /F1 12 Tf 20 100 Td (Max) Tj ET EMC 0 0 m 50 60 l S");
        var plain = StructurePages.Drawn("0 0 1 rg 10 10 30 30 re f BT /F1 12 Tf 20 100 Td (Max) Tj ET 0 0 m 50 60 l S");
        Assert.Contains("[M]", plain);
        Assert.Equal(plain, marked);
    }

    /// <summary>
    /// 7.8.2, sentence 35: BX and EX belong neither to a graphics object nor to the graphics state — between the segments of a path and inside
    /// a text object (around Tf) they change nothing: the path is drawn whole, the text with its font and size.
    /// </summary>
    [Fact]
    public void CompatibilitySection_NoPartOfPathOrTextState()
    {
        Assert.Equal(StructurePages.Drawn("0 0 m 100 0 l 100 100 l f"), StructurePages.Drawn("0 0 m BX 100 0 l EX 100 100 l f"));
        var text = StructurePages.Drawn("BT BX /F1 24 Tf EX 20 100 Td (Erika) Tj ET");
        Assert.Contains("[E]", text);
        Assert.Equal(StructurePages.Drawn("BT /F1 24 Tf 20 100 Td (Erika) Tj ET"), text);
    }

    /// <summary>
    /// 7.8.3, Table 34, /Pattern and /Shading (sentences 27, 29): dictionaries from names to patterns and shadings — as a writer the page keeps
    /// the ones its content names (scn with a pattern, sh) and loses the others (7.8.3, sentences 13 and 15).
    /// </summary>
    [Fact]
    public void Pages_PatternAndShadingResources_TheUsedOnesKept()
    {
        const string content = "/Pattern cs /P1 scn 0 0 100 100 re f q /Sh1 sh Q";
        const string shading = "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>";
        var file = FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Pattern << /P1 << /PatternType 2 /Shading {shading} >> /P2 << /PatternType 2 /Shading {shading} >> >> " +
            $"/Shading << /Sh1 {shading} /Sh2 {shading} >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        using var saved = Saved(file, new PdfSaveOptions { Version = "1.7" });
        var resources = saved.Pages[0].Resources!;
        Assert.Equal(["P1"], resources.GetDictionary(PdfName.Get("Pattern"))!.Entries.Select(e => e.Key.Value));
        Assert.Equal(["Sh1"], resources.GetDictionary(PdfName.Get("Shading"))!.Entries.Select(e => e.Key.Value));
    }

    /// <summary>
    /// 7.9.1, sentences 5 and 6: the rules of text strings hold outside content streams only — in a content stream the bytes of a string select
    /// glyphs through the font. The bytes FE FF 30 41 shown in a simple font are four codes, not a byte order mark and one character: four
    /// glyphs are placed, „þ“, „ÿ“, „0“ and „A“, while the same bytes as a text string in the document are the one character U+3041.
    /// </summary>
    [Fact]
    public void ContentStrings_GlyphsByTheFont_NoByteOrderMark()
    {
        using var doc = PdfDocument.Open(StructurePages.WithContent("BT /F1 12 Tf 20 100 Td <FEFF3041> Tj ET"));
        var glyphs = PageInterpreter.Build(doc, doc.Pages[0]).Text;
        Assert.Equal(["þ", "ÿ", "0", "A"], glyphs.Select(g => g.Text));
        Assert.Equal("ぁ", new PdfString([0xFE, 0xFF, 0x30, 0x41]).ToText());
    }

    /// <summary>
    /// 7.9.1, Table 35, text stream (sentence 34; 7.9.3): a script given as a stream is text like a text string — the format script of a field
    /// written as a stream names the same format as written as a string.
    /// </summary>
    [Fact(Skip = "Zurückgestellt: Textströme werden nicht gelesen, ein Formatskript als Strom bleibt unerkannt")]
    public void TextStream_FormatScriptAsStream_ReadLikeAString()
    {
        const string script = "AFNumber_Format(2, 2, 0, 0, \"\", false);";
        FieldFormat Format(string js)
        {
            using var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
                $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (Betrag) /Rect [10 10 100 30] /P 3 0 R /AA << /F << /S /JavaScript /JS {js} >> >> >>",
                $"<< /Length {script.Length} >>\nstream\n{script}\nendstream"));
            return FieldFormat.Of(FormField.Of(doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!.AsDictionary()!)!);
        }
        Assert.Equal(FieldFormatKind.Number, Format($"({script})").Kind);
        Assert.Equal(FieldFormatKind.Number, Format("5 0 R").Kind);
    }

    /// <summary>
    /// 7.9.6, sentence 28: a key is looked up by going down the tree in order to the leaf whose /Names holds it, a kid whose /Limits exclude
    /// the key passed over — „kilo“ stands in the second leaf (limits h to m) and is found there, though a first leaf whose limits (a to f)
    /// exclude it lists it as well; „alpha“ is found in the first leaf, „zulu“ nowhere.
    /// </summary>
    [Fact]
    public void NameTree_Lookup_GoesDownByTheLimits()
    {
        using var doc = PdfDocument.Open(FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /Dests 5 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Kids [6 0 R 7 0 R] >>",
            "<< /Limits [(a) (f)] /Names [(alpha) [4 0 R /Fit] (kilo) [4 0 R /Fit]] >>",
            "<< /Limits [(h) (m)] /Names [(kilo) [3 0 R /Fit]] >>"));
        int? Page(string name) => PdfOutline.ExplicitDestination(doc, new PdfString(Encoding.ASCII.GetBytes(name))) is { } array && array[0] is PdfReference r
            ? doc.Pages.First(p => p.Id == r.Id).Index : null;
        Assert.Equal(0, Page("kilo"));
        Assert.Equal(1, Page("alpha"));
        Assert.Null(Page("zulu"));
    }
}
