using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Hidden content in the document: "Clean up" removes what nobody sees, and keeps what is visible
/// or what something can make visible. Also redaction against hidden text under the bar.
/// </summary>
public sealed class HiddenContentTests
{
    private static PdfString S(string text) => new(PdfTextEncoding.Encode(text));

    private static bool Contains(byte[] data, string word) => PdfKnead.Cli.LeakSearch.Find(data, [word], null, out _).Count > 0;

    private static byte[] Write(PdfDocument doc, IReadOnlyList<PageItem> pages, PdfSaveOptions options)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, pages, output, options);
        return output.ToArray();
    }

    private static byte[] Clean(byte[] data, CleanupItems items)
    {
        using var doc = PdfDocument.Open(data);
        return Write(doc, PageItem.From(doc), new PdfSaveOptions { Cleanup = items });
    }

    private static List<CleanupFinding> Inspect(byte[] data)
    {
        using var doc = PdfDocument.Open(data);
        return DocumentCleanup.Inspect(doc, PageItem.From(doc));
    }

    private static PdfStream Appearance(string text, double w = 150, double h = 20) =>
        new(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, w, h)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3))))))), Encoding.ASCII.GetBytes($"BT /F1 10 Tf 2 5 Td ({text}) Tj ET"));

    /// <summary>Builds a file with the visible text "Sichtbar" plus whatever <paramref name="setup"/> adds.</summary>
    private static byte[] Build(Action<TestPdf, PdfDictionary, PdfDictionary> setup)
    {
        var pdf = TestPdf.Simple(1);
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        var page = (PdfDictionary)pdf.Get(5);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET"u8.ToArray()));
        setup(pdf, catalog, page);
        return pdf.Build();
    }

    private static byte[] Content(string content) =>
        Build((pdf, _, _) => pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes("BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET " + content))));

    private static void Fields(TestPdf pdf, PdfDictionary catalog, PdfDictionary page, params PdfDictionary[] widgets)
    {
        var refs = new PdfArray();
        foreach (var widget in widgets)
        {
            widget.Set(PdfName.Get("P"), TestPdf.Ref(5));
            refs.Add(TestPdf.Ref(pdf.Add(widget)));
        }
        page.Set(PdfName.Get("Annots"), refs);
        catalog.Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", refs)));
    }

    private static PdfDictionary TextField(string name, string value, long flags, PdfArray rect) =>
        TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("FT", TestPdf.N("Tx")), ("T", S(name)), ("V", S(value)),
            ("F", PdfInteger.Of(flags)), ("Rect", rect), ("AP", TestPdf.Dict(("N", Appearance(value)))));

    private static PdfDictionary Button(string name, PdfDictionary action) =>
        TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("FT", TestPdf.N("Btn")), ("T", S(name)),
            ("Rect", TestPdf.Box(300, 450, 315, 465)), ("F", PdfInteger.Of(4)), ("A", action));

    /// <summary>Cases that nobody sees: name, hidden word, file.</summary>
    public static TheoryData<string, string> UnseenCases => new()
    {
        { "Feld ausgeblendet (Hidden)", "VERSTECKTFELD" },
        { "Feld nicht anzeigen (NoView)", "NOVIEWFELD" },
        { "Feld ohne Fläche", "NULLFELD" },
        { "Feld außerhalb der Seite", "AUSSENFELD" },
        { "Text außerhalb der CropBox", "AUSSERHALBCROP" },
        { "Text weit außerhalb", "WEITWEG" },
        { "Form nie gezeichnet", "UNGENUTZTEFORM" },
        { "Schriftgröße 0", "GROESSENULL" },
        { "weg-geclippt", "WEGGECLIPPT" },
    };

    private static byte[] Case(string word) => word switch
    {
        "VERSTECKTFELD" => Build((pdf, cat, page) => Fields(pdf, cat, page, TextField("Geheim", word, 2, TestPdf.Box(300, 500, 450, 520)))),
        "NOVIEWFELD" => Build((pdf, cat, page) => Fields(pdf, cat, page, TextField("Geheim", word, 32, TestPdf.Box(300, 500, 450, 520)))),
        "NULLFELD" => Build((pdf, cat, page) => Fields(pdf, cat, page, TextField("Geheim", word, 4, TestPdf.Box(300, 500, 300, 500)))),
        "AUSSENFELD" => Build((pdf, cat, page) => Fields(pdf, cat, page, TextField("Geheim", word, 4, TestPdf.Box(-900, -900, -750, -880)))),
        "AUSSERHALBCROP" => Build((pdf, _, page) =>
        {
            page.Set(PdfName.Get("CropBox"), TestPdf.Box(0, 0, 300, 300));
            pdf.Replace(4, new PdfStream(new PdfDictionary(), "BT /F1 20 Tf 72 100 Td (Sichtbar) Tj ET BT /F1 20 Tf 72 700 Td (AUSSERHALBCROP) Tj ET"u8.ToArray()));
        }),
        "WEITWEG" => Content("BT /F1 20 Tf -5000 -5000 Td (WEITWEG) Tj ET"),
        "UNGENUTZTEFORM" => Build((pdf, _, page) =>
            page.Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))), ("XObject", TestPdf.Dict(("Fx0", TestPdf.Ref(pdf.Add(Appearance(word))))))))),
        "GROESSENULL" => Content("BT /F1 0 Tf 72 700 Td (GROESSENULL) Tj ET"),
        "WEGGECLIPPT" => Content("q 0 0 0 0 re W n BT /F1 20 Tf 72 600 Td (WEGGECLIPPT) Tj ET Q"),
        _ => throw new ArgumentException(word),
    };

    [Theory]
    [MemberData(nameof(UnseenCases))]
    public void HiddenContent_RemovesWhatNobodySees(string name, string word)
    {
        var data = Case(word);
        Assert.True(Contains(data, word), name);
        Assert.Contains(Inspect(data), f => f.Item == CleanupItems.HiddenContent && f.Count > 0);
        var cleaned = Clean(data, CleanupItems.HiddenContent);
        Assert.False(Contains(cleaned, word), $"{name}: noch in der Datei");
        Assert.True(Contains(cleaned, "Sichtbar"), $"{name}: Sichtbares fehlt");
        Assert.True(Contains(Clean(data, CleanupItems.None), word), $"{name}: ohne Bereinigen schon weg");
    }

    [Fact]
    public void VisibleThings_Stay()
    {
        // Visible when printed (NoView with Print), cut off at the edge, small but legible
        var data = Build((pdf, cat, page) =>
        {
            Fields(pdf, cat, page, TextField("Druck", "DRUCKFELD", 32 | 4, TestPdf.Box(300, 500, 450, 520)));
            pdf.Replace(4, new PdfStream(new PdfDictionary(), "BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET BT /F1 20 Tf -30 700 Td (RANDTEXT) Tj ET BT /F1 2 Tf 72 650 Td (KLEINTEXT) Tj ET"u8.ToArray()));
        });
        var cleaned = Clean(data, CleanupItems.HiddenContent | CleanupItems.SuspiciousText);
        foreach (var word in new[] { "Sichtbar", "DRUCKFELD", "NDTEXT", "KLEINTEXT" }) Assert.True(Contains(cleaned, word), word);
        // At the edge: "R" and "A" lie completely beside the page (Helvetica 20 pt: −30 … −2.2); they are removed, the rest from the cut-off
        // "N" on stays
        Assert.False(Contains(cleaned, "RANDTEXT"));
    }

    [Fact]
    public void HiddenField_ShownByScript_StaysUntilScriptsGo()
    {
        var data = Build((pdf, cat, page) => Fields(pdf, cat, page,
            TextField("Geheimfeld", "SKRIPTFELD", 2, TestPdf.Box(300, 500, 450, 520)),
            Button("Zeigen", TestPdf.Dict(("S", TestPdf.N("JavaScript")), ("JS", S("this.getField('Geheimfeld').display = display.visible;"))))));
        var finding = Assert.Single(Inspect(data), f => f.Item == CleanupItems.HiddenContent);
        Assert.Equal(0, finding.Count);
        Assert.Contains("Geheimfeld", finding.Detail);
        Assert.True(Contains(Clean(data, CleanupItems.HiddenContent), "SKRIPTFELD"));
        Assert.False(Contains(Clean(data, CleanupItems.HiddenContent | CleanupItems.Scripts), "SKRIPTFELD"));
    }

    [Fact]
    public void HiddenField_ShownByHideAction_Stays()
    {
        // A "Hide" action with /H false shows the field; it is not a script action, so the field stays even with "clean up scripts"
        var data = Build((pdf, cat, page) => Fields(pdf, cat, page,
            TextField("Antwort", "AKTIONSFELD", 2, TestPdf.Box(300, 500, 450, 520)),
            Button("Zeigen", TestPdf.Dict(("S", TestPdf.N("Hide")), ("T", S("Antwort")), ("H", PdfBoolean.False)))));
        Assert.True(Contains(Clean(data, CleanupItems.HiddenContent | CleanupItems.Scripts), "AKTIONSFELD"));
    }

    [Fact]
    public void SuspiciousText_OnlyOnRequest()
    {
        var white = Content("BT /F1 20 Tf 1 g 72 700 Td (WEISSAUFWEISS) Tj ET");
        var covered = Content("BT /F1 20 Tf 72 700 Td (VERDECKT) Tj ET 1 0 0 rg 60 690 300 40 re f");
        var onDark = Content("0 0 0 rg 60 640 300 40 re f BT /F1 20 Tf 1 g 72 650 Td (WEISSAUFSCHWARZ) Tj ET");
        foreach (var (data, word) in new[] { (white, "WEISSAUFWEISS"), (covered, "VERDECKT") })
        {
            Assert.Contains(Inspect(data), f => f.Item == CleanupItems.SuspiciousText && f.Detail.Contains(word[..4]));
            Assert.True(Contains(Clean(data, CleanupItems.HiddenContent), word), word + ": nur auf Wunsch");
            Assert.False(Contains(Clean(data, CleanupItems.SuspiciousText), word), word);
            Assert.True(Contains(Clean(data, CleanupItems.SuspiciousText), "Sichtbar"), word + ": Sichtbares fehlt");
        }
        Assert.DoesNotContain(Inspect(onDark), f => f.Item == CleanupItems.SuspiciousText);
        Assert.True(Contains(Clean(onDark, CleanupItems.SuspiciousText), "WEISSAUFSCHWARZ"));
    }

    // ------------------------------------------------------------ Redaction

    public static TheoryData<string, string> RedactionTricks => new()
    {
        { "BALKENNULL", "BT /F1 0 Tf 80 650 Td (BALKENNULL) Tj ET" },
        { "BALKENTZ", "BT /F1 20 Tf 0 Tz 80 650 Td (BALKENTZ) Tj ET" },
        { "BALKENUNSICHTBAR", "BT /F1 20 Tf 3 Tr 80 650 Td (BALKENUNSICHTBAR) Tj ET" },
        { "BALKENCLIP", "q 0 0 1 1 re W n BT /F1 20 Tf 80 650 Td (BALKENCLIP) Tj ET Q" },
        { "BALKENWEISS", "BT /F1 20 Tf 1 g 80 650 Td (BALKENWEISS) Tj ET" },
        { "BALKENTC", "BT /F1 20 Tf 5000 Tc 80 650 Td (BALKENTC) Tj ET" },
        { "BALKENSPIEGEL", "BT /F1 20 Tf -1 0 0 1 300 650 Tm (BALKENSPIEGEL) Tj ET" },
        { "BALKENMATRIX", "BT /F1 20 Tf 0 0 0 0 80 650 Tm (BALKENMATRIX) Tj ET" },
        { "BALKENBBOX", "q 1 0 0 1 80 650 cm /Fx0 Do Q" },
        { "BALKENKLEIN", "q 0.001 0 0 0.001 100 650 cm BT /F1 20 Tf (BALKENKLEIN) Tj ET Q" },
    };

    [Theory]
    [MemberData(nameof(RedactionTricks))]
    public void Redaction_RemovesHiddenTextUnderTheBar(string word, string content)
    {
        var pdf = TestPdf.Simple(1);
        var page = (PdfDictionary)pdf.Get(5);
        var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 1, 1)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3))))))), "BT /F1 20 Tf 0 0 Td (BALKENBBOX) Tj ET"u8.ToArray()));
        page.Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))), ("XObject", TestPdf.Dict(("Fx0", TestPdf.Ref(form))))));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes("BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET " + content)));
        var data = pdf.Build();
        Assert.True(Contains(data, word));
        using var doc = PdfDocument.Open(data);
        var item = PageItem.From(doc)[0].WithElements([new RedactionElement { Rect = new PdfRectangle(72, 560, 400, 720) }]);
        var redacted = Write(doc, [item], new PdfSaveOptions { ApplyRedactions = true });
        Assert.False(Contains(redacted, word));
        Assert.True(Contains(redacted, "Sichtbar"));
    }

    // ---- 7.1: visibility expressions (/VE) of membership dictionaries (ISO 32000-2, 8.11.2.2; docs/FEHLERGEDAECHTNIS.md, No. 561)

    /// <summary>
    /// A page whose text „Max Mustermann“ is marked with an OCMD (object 7) over the layers A (5) and B (6); <paramref name="off"/> lists the
    /// layers that are off in the default configuration.
    /// </summary>
    private static byte[] WithMembership(string ocmd, string off) => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R/OCProperties<</OCGs[5 0 R 6 0 R]/D<</OFF[" + off + "]>>>>>>",
        [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 8 0 R>>/Properties<</M1 7 0 R>>>>>>",
        [4] = ("", "/OC /M1 BDC BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET EMC BT /F1 12 Tf 72 600 Td (Immer da) Tj ET"),
        [5] = "<</Type/OCG/Name(A)>>",
        [6] = "<</Type/OCG/Name(B)>>",
        [7] = ocmd,
        [8] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
    });

    private static bool Shown(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        var text = string.Concat(PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => t.Text));
        Assert.Contains("Immer da", text);
        return text.Contains("Max Mustermann", StringComparison.Ordinal);
    }

    /// <summary>/VE takes precedence over /OCGs and /P: A and B on, /P AllOn says visible, [/Not A] says hidden — hidden. Before: visible.</summary>
    [Fact]
    public void VisibilityExpression_TakesPrecedenceOverMembership() =>
        Assert.False(Shown(WithMembership("<</Type/OCMD/OCGs[5 0 R 6 0 R]/P/AllOn/VE[/Not 5 0 R]>>", "")));

    /// <summary>An OCMD with /VE alone: [/Not [/Or A B]] — both off: shown; A on: hidden. Before: always shown.</summary>
    [Fact]
    public void VisibilityExpression_WithoutOcgs_IsEvaluated()
    {
        Assert.True(Shown(WithMembership("<</Type/OCMD/VE[/Not[/Or 5 0 R 6 0 R]]>>", "5 0 R 6 0 R")));
        Assert.False(Shown(WithMembership("<</Type/OCMD/VE[/Not[/Or 5 0 R 6 0 R]]>>", "6 0 R")));
        Assert.True(Shown(WithMembership("<</Type/OCMD/VE[/And 5 0 R[/Not 6 0 R]]>>", "6 0 R")));
        Assert.False(Shown(WithMembership("<</Type/OCMD/VE[/And 5 0 R 6 0 R]>>", "6 0 R")));
    }

    /// <summary>The public test file with five expressions over three layers (one off): exactly two of the five are hidden. Before: none.</summary>
    [TestPdfsFact]
    public void VisibilityExpressions_TwoOfFiveHidden()
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(TestPdfs.File("pdfjs", "visibility_expressions.pdf")));
        var layers = OptionalContent.For(doc);
        var ocmds = Enumerable.Range(1, doc.HighestObjectNumber).Select(n => doc.GetObject(n)).OfType<PdfDictionary>()
            .Where(d => d.IsType(PdfName.Get("OCMD")) && d.ContainsKey(PdfName.Get("VE"))).ToList();
        Assert.Equal(5, ocmds.Count);
        Assert.Equal(2, ocmds.Count(m => layers.IsHidden(m, null)));
    }

    /// <summary>„Inhalte ausgeblendeter Ebenen“ removes what /VE hides and keeps what it shows (judged as the display does).</summary>
    [Fact]
    public void VisibilityExpression_HiddenContentIsCleanedUp()
    {
        var hidden = Clean(WithMembership("<</Type/OCMD/OCGs[5 0 R 6 0 R]/P/AllOn/VE[/Not 5 0 R]>>", ""), CleanupItems.HiddenLayers);
        Assert.False(Contains(hidden, "Max Mustermann"));
        var shown = Clean(WithMembership("<</Type/OCMD/OCGs[5 0 R 6 0 R]/P/AllOff/VE[/And 5 0 R 6 0 R]>>", ""), CleanupItems.HiddenLayers);
        Assert.True(Contains(shown, "Max Mustermann"));
    }

    /// <summary>Guards: very deep, cyclic and invalid expressions fall back to /OCGs and /P without failing (green before as well).</summary>
    [Fact]
    public void VisibilityExpression_DeepCyclicInvalid_FallBackToP()
    {
        var deep = string.Concat(Enumerable.Repeat("[/Not", 200)) + " 5 0 R" + new string(']', 200);
        Assert.True(Shown(WithMembership("<</Type/OCMD/OCGs[5 0 R]/P/AnyOn/VE" + deep + ">>", "")));
        Assert.True(Shown(WithMembership("<</Type/OCMD/OCGs[5 0 R]/P/AnyOn/VE[/Foo 5 0 R]>>", "")));
        Assert.True(Shown(WithMembership("<</Type/OCMD/OCGs[5 0 R]/P/AnyOn/VE[/Not 5 0 R 6 0 R]>>", "")));
        // a cycle: the expression names itself (object 9)
        var cyclic = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/OCProperties<</OCGs[5 0 R 6 0 R]/D<</OFF[]>>>>>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 8 0 R>>/Properties<</M1 7 0 R>>>>>>",
            [4] = ("", "/OC /M1 BDC BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET EMC BT /F1 12 Tf 72 600 Td (Immer da) Tj ET"),
            [5] = "<</Type/OCG/Name(A)>>",
            [6] = "<</Type/OCG/Name(B)>>",
            [7] = "<</Type/OCMD/OCGs[5 0 R]/P/AnyOn/VE 9 0 R>>",
            [8] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            [9] = "[/And 5 0 R 9 0 R]",
        });
        Assert.True(Shown(cyclic));
    }

    /// <summary>
    /// A form drawn by the page whose /OC is a membership dictionary (object 7) — not in the /Properties of the page; layers A and B on.
    /// </summary>
    private static byte[] FormWithMembership(string ocmd) => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R/OCProperties<</OCGs[5 0 R 6 0 R]/D<</OFF[]>>>>>>",
        [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 8 0 R>>/XObject<</X1 9 0 R>>>>>>",
        [4] = ("", "/X1 Do BT /F1 12 Tf 72 600 Td (Immer da) Tj ET"),
        [5] = "<</Type/OCG/Name(A)>>",
        [6] = "<</Type/OCG/Name(B)>>",
        [7] = ocmd,
        [8] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
        [9] = ("/Type/XObject/Subtype/Form/BBox[0 0 595 842]/OC 7 0 R/Resources<</Font<</F1 8 0 R>>>>", "BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET"),
    });

    /// <summary>
    /// Content hidden by a membership rule outside the /Properties of the page (docs/FEHLERGEDAECHTNIS.md, No. 590): a form with /OC
    /// &lt;&lt;/VE [/Not A]&gt;&gt;, and one with /OCGs [A] /P /AllOff, both with every layer on — hidden on screen and in print. „Inhalte ausgeblendeter
    /// Ebenen“ names them and removes them. Before only the /Properties of the page were looked at: nothing was found, the name stayed.
    /// </summary>
    [Theory]
    [InlineData("<</Type/OCMD/VE[/Not 5 0 R]>>")]
    [InlineData("<</Type/OCMD/OCGs[5 0 R]/P/AllOff>>")]
    public void MembershipRule_OnAForm_FoundAndCleanedUp(string ocmd)
    {
        var file = FormWithMembership(ocmd);
        Assert.False(Shown(file));
        Assert.Contains(Inspect(file), f => f.Item == CleanupItems.HiddenLayers);
        var cleaned = Clean(file, CleanupItems.HiddenLayers);
        Assert.False(Contains(cleaned, "Max Mustermann"));
        Assert.True(Contains(cleaned, "Immer da"));
    }

    /// <summary>
    /// A layer named in a visibility expression stays when hidden layers are dropped (No. 590): /OCGs [B] /P /AllOff /VE [/Not A] with A off —
    /// the expression shows the content. Before, A was dropped, the expression held null and was invalid, and the fallback over /OCGs and /P
    /// (B on, /AllOff) hid the content after cleaning.
    /// </summary>
    [Fact]
    public void LayerInAnExpression_StaysWhenHiddenLayersAreDropped()
    {
        var file = WithMembership("<</Type/OCMD/OCGs[6 0 R]/P/AllOff/VE[/Not 5 0 R]>>", "5 0 R");
        Assert.True(Shown(file));
        var cleaned = Clean(file, CleanupItems.HiddenLayers);
        Assert.True(Shown(cleaned));
    }

    /// <summary>
    /// A membership dictionary as an operand of a visibility expression is not a layer (ISO 32000-2, 8.11.2.2; docs/FEHLERGEDAECHTNIS.md,
    /// No. 600): the expression [/And M [/Not A] [/Not B]] naming its own OCMD (which the file also lists among the layers, as a public test
    /// file does) is invalid — /P over /OCGs decides, A and B on: shown. Before, the OCMD counted as a layer that is on, and the content was
    /// hidden.
    /// </summary>
    [Fact]
    public void MembershipDictionaryAsOperand_MakesTheExpressionInvalid()
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/OCProperties<</OCGs[7 0 R 5 0 R 6 0 R]/D<</OFF[]>>>>>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 8 0 R>>/Properties<</M1 7 0 R>>>>>>",
            [4] = ("", "/OC /M1 BDC BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET EMC BT /F1 12 Tf 72 600 Td (Immer da) Tj ET"),
            [5] = "<</Type/OCG/Name(A)>>",
            [6] = "<</Type/OCG/Name(B)>>",
            [7] = "<</Type/OCMD/OCGs[5 0 R 6 0 R]/VE[/And 7 0 R[/Not 5 0 R][/Not 6 0 R]]>>",
            [8] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
        });
        Assert.True(Shown(file));
    }
}
