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

    /// <summary>
    /// Saved as 2.0, what 2.0 deprecates or no longer has goes (M6, pass over what is deprecated and gone; docs/FEHLERGEDAECHTNIS.md, No. 1127):
    /// a PostScript XObject leaves the resources (8.8.2; it draws nothing — the PDF/A way did so already, No. 307), /PS and /Subtype2 leave a form,
    /// /TR and /TR2 a graphics state (deprecated, Table 57) — not the /TR of a soft mask —, /BM as an array becomes the name that counts
    /// (11.3.5), CalCMYK becomes DeviceCMYK (8.6.5.1). Saved as 1.7, all of it stays. The PostScript XObject is written as an empty form, so
    /// that its "Do" still names an XObject (No. 1128; before, a reference into the void).
    /// </summary>
    [Fact]
    public void As20_DeprecatedGraphicsGo_As17TheyStay()
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 200 200]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</XObject<</P0 5 0 R/F0 6 0 R>>" +
                  "/ExtGState<</G0<</Type/ExtGState/TR/Identity/TR2/Default/BM[/Unbekannt/Multiply/Screen]" +
                  "/SMask<</Type/Mask/S/Luminosity/G 6 0 R/TR/Identity>>>>>>/ColorSpace<</C0[/CalCMYK<<>>]>>>>>>",
            [4] = ("", "/G0 gs /P0 Do /F0 Do /C0 cs 0 0 0 1 sc 0 0 10 10 re f"),
            [5] = ("/Type/XObject/Subtype/PS", "% PostScript für Drucker"),
            [6] = ("/Type/XObject/Subtype/Form/BBox[0 0 10 10]/Subtype2/PS/PS 5 0 R", "0 0 5 5 re f"),
        });
        byte[] Bytes(string version)
        {
            using var doc = PdfDocument.Open(file);
            using var output = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = version, CompressStreams = false, ObjectStreams = false });
            return output.ToArray();
        }
        string Saved(string version) => Encoding.Latin1.GetString(Bytes(version)).Replace(" ", "");
        var as20 = Saved("2.0");
        // the PostScript XObject became an empty form: the "Do" still names a stream of /XObject (8.8.1; No. 1128)
        using (var doc20 = PdfDocument.Open(Bytes("2.0")))
        {
            var p0 = doc20.Pages[0].Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("P0"));
            var form = Assert.IsType<PdfStream>(p0);
            Assert.Equal("Form", form.Dictionary.GetName(PdfName.Subtype)?.Value);
        }
        Assert.DoesNotContain("/Subtype/PS", as20);
        Assert.DoesNotContain("/Subtype2", as20);
        Assert.DoesNotContain("/TR2", as20);
        Assert.Contains("/TR/Identity>>", as20);          // the soft mask's own transfer function stays …
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(as20, "/TR/"));   // … and only it
        Assert.Contains("/BM/Multiply", as20);
        Assert.DoesNotContain("CalCMYK", as20);
        Assert.Contains("/C0/DeviceCMYK", as20);
        var as17 = Saved("1.7");
        foreach (var kept in new[] { "/P0", "/Subtype/PS", "/Subtype2/PS", "/TR2/Default", "/BM[/Unbekannt/Multiply/Screen]", "/CalCMYK" })
            Assert.Contains(kept, as17);
    }

    /// <summary>
    /// Saved as 2.0, only the transfer functions of graphics states go (Table 57): /TR in the role map (the table row of the structure) and in
    /// the class map (an attribute object) is no transfer function and stays, as the structure would lose its table rows otherwise; a graphics
    /// state's /TR as a function, as a reference to one and as an array of four goes (docs/FEHLERGEDAECHTNIS.md, No. 1135; before, every
    /// dictionary without /Type lost its /TR).
    /// </summary>
    [Fact]
    public void As20_TrOfRoleMapAndClassMapStays_TransferFunctionsGo()
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/MarkInfo<</Marked true>>/StructTreeRoot 7 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 200 200]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</ExtGState<</G0<</TR 5 0 R/LW 1>>/G1 6 0 R>>>>>>",
            [4] = ("", "/G0 gs /G1 gs 0 0 10 10 re f"),
            [5] = "<</FunctionType 2/Domain[0 1]/C0[0]/C1[1]/N 1>>",
            [6] = "<</TR2[5 0 R 5 0 R 5 0 R/Identity]/LW 2>>",
            [7] = "<</Type/StructTreeRoot/RoleMap<</TR/TR/Zeile/TR>>/ClassMap<</TR 8 0 R>>>>",
            [8] = "<</O/Layout/Placement/Block>>",
        });
        byte[] Bytes(string version)
        {
            using var doc = PdfDocument.Open(file);
            using var output = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = version, CompressStreams = false, ObjectStreams = false });
            return output.ToArray();
        }
        using var doc20 = PdfDocument.Open(Bytes("2.0"));
        var tree = doc20.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!;
        Assert.Equal("TR", tree.GetDictionary(PdfName.Get("RoleMap"))!.GetName(PdfName.Get("TR"))?.Value);
        Assert.Equal("TR", tree.GetDictionary(PdfName.Get("RoleMap"))!.GetName(PdfName.Get("Zeile"))?.Value);
        Assert.Equal("Layout", tree.GetDictionary(PdfName.Get("ClassMap"))!.GetDictionary(PdfName.Get("TR"))!.GetName(PdfName.Get("O"))?.Value);
        var states = doc20.Pages[0].Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("ExtGState"))!;
        Assert.False(states.GetDictionary(PdfName.Get("G0"))!.ContainsKey(PdfName.Get("TR")));
        Assert.False(states.GetDictionary(PdfName.Get("G1"))!.ContainsKey(PdfName.Get("TR2")));
        Assert.Equal(2, states.GetDictionary(PdfName.Get("G1"))!.GetNumber(PdfName.Get("LW")));
        using var doc17 = PdfDocument.Open(Bytes("1.7"));
        var states17 = doc17.Pages[0].Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("ExtGState"))!;
        Assert.True(states17.GetDictionary(PdfName.Get("G0"))!.ContainsKey(PdfName.Get("TR")));
        Assert.True(states17.GetDictionary(PdfName.Get("G1"))!.ContainsKey(PdfName.Get("TR2")));
    }

    /// <summary>
    /// 8.9.7: saved as 2.0, a page whose inline image of 5 000 bytes becomes an image XObject gets new contents — the contents as read are not
    /// written as well, as an object nothing names (docs/FEHLERGEDAECHTNIS.md, No. 1135; before, they stood in the file a second time, with
    /// the image data): every object of the copy is named.
    /// </summary>
    [Fact]
    public void As20_LargeInlineImage_TheOldContentsAreNotWrittenToo()
    {
        var samples = string.Concat(Enumerable.Repeat("\x81\x82\x83\x84\x86", 1000));
        var content = Encoding.Latin1.GetBytes("q 100 0 0 50 0 0 cm BI /W 100 /H 50 /BPC 8 /CS /G ID " + samples + " EI Q");
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 200 200]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>>>",
            [4] = ("/Filter/FlateDecode", Encoding.Latin1.GetString(Core.Pdf.Filters.StrongDeflate.Encode(content))),
        });
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "2.0", ObjectStreams = false });
        using var copy = PdfDocument.Open(output.ToArray());
        using (var written = Core.Pdf.Content.PageInterpreter.PageContent(copy, copy.Pages[0].Dictionary))
            Assert.DoesNotContain("BI", Encoding.Latin1.GetString(written.Span));
        // every object of the copy is named from the trailer (the cross-reference stream aside)
        var reached = new HashSet<int>();
        void Walk(PdfObject value)
        {
            switch (value)
            {
                case PdfReference r when reached.Add(r.Id.Number):
                    Walk(copy.Resolve(r.Id));
                    break;
                case PdfStream st:
                    Walk(st.Dictionary);
                    break;
                case PdfDictionary d:
                    foreach (var (_, item) in d.Entries) Walk(item);
                    break;
                case PdfArray a:
                    foreach (var item in a) Walk(item);
                    break;
            }
        }
        Walk(copy.Trailer);
        var unnamed = Enumerable.Range(1, copy.ObjectCount + 4)
            .Where(n => !reached.Contains(n) && copy.Resolve(new PdfObjectId(n, 0)) is PdfStream or PdfDictionary or PdfArray &&
                        !(copy.Resolve(new PdfObjectId(n, 0)) is PdfStream x && x.Dictionary.GetName(PdfName.Type)?.Value == "XRef"))
            .ToList();
        Assert.Empty(unnamed);
    }

    /// <summary>
    /// 8.6.6.5: ISO 32000-1 allowed Lab as the process space of NChannel, 2.0 does not — saved as 2.0 the attributes lose /Process and become
    /// plain DeviceN attributes (L, a, b stay colorants through the tint transform, as the display draws them); saved as 1.7 they stay
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1128).
    /// </summary>
    [Fact]
    public void As20_NChannelWithLabProcess_BecomesDeviceN_As17Stays()
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 200 200]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</ColorSpace<</N0[/DeviceN[/L/a/b/Schmuck]/DeviceRGB 5 0 R" +
                  "<</Subtype/NChannel/Process<</ColorSpace[/Lab<</WhitePoint[0.9505 1 1.089]>>]/Components[/L/a/b]>>>>]>>>>>>",
            [4] = ("", "/N0 cs 50 0 0 1 scn 0 0 100 100 re f"),
            [5] = ("/FunctionType 4/Domain[0 100 -128 127 -128 127 0 1]/Range[0 1 0 1 0 1]", "{ pop pop pop pop 0.5 0.5 0.5 }"),
        });
        string Saved(string version)
        {
            using var doc = PdfDocument.Open(file);
            using var output = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = version, CompressStreams = false, ObjectStreams = false });
            return Encoding.Latin1.GetString(output.ToArray()).Replace(" ", "");
        }
        var as20 = Saved("2.0");
        Assert.DoesNotContain("/Process", as20);
        Assert.DoesNotContain("/NChannel", as20);
        Assert.Contains("/Subtype/DeviceN", as20);
        var as17 = Saved("1.7");
        Assert.Contains("/Subtype/NChannel/Process", as17);
    }
}
