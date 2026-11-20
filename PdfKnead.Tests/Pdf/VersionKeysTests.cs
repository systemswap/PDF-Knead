using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Keys that do not belong to the version written leave the copy, or raise its version (docs/FEHLERGEDAECHTNIS.md, No. 702; found by the runs of
/// all ways to write against the file-format model).
/// </summary>
public sealed class VersionKeysTests
{
    /// <summary>
    /// A file with keys of several versions: a page with /Tabs, a field with the comb flag (PDF 1.5), a graphics state with /UseBlackPtComp
    /// and /HTO (2.0), resources with /ProcSet, a font with /Name and a descriptor with /CharSet, mark information with /Suspects (deprecated
    /// in 2.0), and /AF on the page (2.0, kept below it).
    /// </summary>
    private static byte[] Source() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R/AcroForm<</Fields[8 0 R]>>/MarkInfo<</Marked true/Suspects false>>>>",
        [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Tabs/R/Annots[8 0 R]/AF[]" +
              "/Resources<</ProcSet[/PDF/Text]/Font<</F1 5 0 R>>/ExtGState<</G0<</Type/ExtGState/UseBlackPtComp/ON/HTO[0 0]/ca 1>>>>>>>>",
        [4] = ("", "/G0 gs BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET"),
        [5] = "<</Type/Font/Subtype/Type1/Name/F1/BaseFont/Helvetica/FontDescriptor 6 0 R>>",
        [6] = "<</Type/FontDescriptor/FontName/Helvetica/Flags 32/FontBBox[0 0 1000 1000]/ItalicAngle 0/Ascent 700/Descent -200/CapHeight 700/StemV 80/CharSet(/M/a/x)>>",
        [8] = "<</Type/Annot/Subtype/Widget/FT/Tx/T(Nummer)/Ff 16777216/MaxLen 6/Rect[72 600 172 620]/P 3 0 R>>",
    });

    private static (string Text, PdfSaveResult Result) Saved(string version, string? part = null)
    {
        using var doc = PdfDocument.Open(Source());
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = version, CompressStreams = false, ObjectStreams = false });
        return (Encoding.Latin1.GetString(output.ToArray()), result);
    }

    [Fact]
    public void Below15_TabsGo_FieldFlagsOf15RaiseTheVersion()
    {
        var (text, result) = Saved("1.4");
        Assert.DoesNotContain("/Tabs", text);
        Assert.Contains("/Ff 16777216", text);   // the comb flag stays …
        Assert.Equal("1.5", result.Version);       // … and the file says the version it needs
        Assert.Contains("/Version/1.5", text.Replace(" ", ""));
        Assert.DoesNotContain("/UseBlackPtComp", text);
        Assert.DoesNotContain("/HTO", text);
    }

    [Fact]
    public void Below20_GraphicsStateKeysOf20Go_AssociatedFilesStay()
    {
        var (text, _) = Saved("1.7");
        Assert.DoesNotContain("/UseBlackPtComp", text);
        Assert.DoesNotContain("/HTO", text);
        Assert.Contains("/Tabs", text);
        Assert.Contains("/ProcSet", text);   // not deprecated before 2.0
    }

    [Fact]
    public void As20_DeprecatedKeysGo()
    {
        var (text, _) = Saved("2.0");
        Assert.DoesNotContain("/ProcSet", text);
        Assert.DoesNotContain("/Name/F1", text.Replace(" ", ""));
        Assert.DoesNotContain("/CharSet", text);
        Assert.DoesNotContain("/Suspects", text);
        Assert.Contains("/UseBlackPtComp", text);   // a key of 2.0 stays
        Assert.Contains("/Tabs", text);
    }

    /// <summary>
    /// The /Name of a Type 3 font stays in 2.0: ISO 32000-2 deprecates it for simple fonts (Table 109), not for Type 3 fonts (Table 110), and the
    /// model of the format asks the descriptor's /FontName to equal it — before, it went like that of a Type 1 font, and the descriptor named a
    /// font no longer there (docs/FEHLERGEDAECHTNIS.md, No. 812; external checkers over the save test, issue14953).
    /// </summary>
    [Fact]
    public void As20_NameOfAType3FontStays()
    {
        using var doc = PdfDocument.Open(CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 5 0 R/F2 8 0 R>>>>>>",
            [4] = ("", "BT /F1 12 Tf 72 700 Td (a) Tj /F2 12 Tf (b) Tj ET"),
            [5] = "<</Type/Font/Subtype/Type3/Name/Muster3/FontBBox[0 0 750 750]/FontMatrix[0.001 0 0 0.001 0 0]/CharProcs<</a 7 0 R>>" +
                  "/Encoding<</Type/Encoding/Differences[97/a]>>/FirstChar 97/LastChar 97/Widths[1000]/FontDescriptor 6 0 R/Resources<<>>>>",
            [6] = "<</Type/FontDescriptor/FontName/Muster3/Flags 4/ItalicAngle 0>>",
            [7] = ("", "1000 0 0 0 750 750 d1 0 0 750 750 re f"),
            [8] = "<</Type/Font/Subtype/Type1/Name/Einfach/BaseFont/Helvetica>>",
        }));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "2.0", CompressStreams = false, ObjectStreams = false });
        var text = Encoding.Latin1.GetString(output.ToArray()).Replace(" ", "");
        Assert.Contains("/Name/Muster3", text);
        Assert.Contains("/FontName/Muster3", text);
        Assert.DoesNotContain("/Name/Einfach", text);   // that of the Type 1 font goes as before
    }
    /// <summary>
    /// /Tabs /S of a tagged file stays below 1.5: PDF/UA asks for it on every page with annotations (ISO 14289-1, 7.18.3) and a reader of 1.4
    /// passes over it — before, the copy as 1.4 lost it and kept its PDF/UA claim (docs/FEHLERGEDAECHTNIS.md, No. 921; runs of all ways to
    /// write, M5). /Tabs of an untagged file goes as before (Below15_TabsGo_FieldFlagsOf15RaiseTheVersion).
    /// </summary>
    [Fact]
    public void Below15_TabsOfTheStructureStayInATaggedFile()
    {
        using var doc = PdfDocument.Open(CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/Lang(de)/MarkInfo<</Marked true>>/StructTreeRoot 6 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Tabs/S/Annots[5 0 R]/StructParents 0/Resources<<>>>>",
            [4] = ("", "/Artifact BMC 0 0 m 100 100 l S EMC"),
            [5] = "<</Type/Annot/Subtype/Text/Rect[100 100 120 120]/F 4/Contents(Notiz)/P 3 0 R/StructParent 1>>",
            [6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree<</Nums[0[] 1 8 0 R]>>/ParentTreeNextKey 2>>",
            [7] = "<</Type/StructElem/S/Document/P 6 0 R/K 8 0 R>>",
            [8] = "<</Type/StructElem/S/Annot/P 7 0 R/Pg 3 0 R/K<</Type/OBJR/Obj 5 0 R>>>>",
        }));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "1.4", CompressStreams = false, ObjectStreams = false });
        var text = Encoding.Latin1.GetString(output.ToArray()).Replace(" ", "");
        Assert.Contains("/Tabs/S", text);
        Assert.StartsWith("%PDF-1.4", text);   // nothing raised for it
    }
}
