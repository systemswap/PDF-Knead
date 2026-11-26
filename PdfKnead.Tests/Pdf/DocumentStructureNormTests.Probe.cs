using System.Text;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Document structure, 7.7.3.3, 7.9.2.3 and 7.11: one test per sentence where a row of the report named a test that checked only the
/// behaviour next to it (page group, beads in reading order, templates without beads, the order of the tab key as checked, /UF before /F in
/// links, the date of an embedded file, one byte per character in PDFDocEncoding).
/// </summary>
public sealed partial class DocumentStructureNormTests
{
    private static PdfString Ascii(string text) => new(Encoding.ASCII.GetBytes(text));

    /// <summary>
    /// 7.7.3.3, Table 31, /Group (sentences 57 and 58): the group attributes dictionary of the page's page group is written with the page as it
    /// stands — a dictionary with /S /Transparency, its colour space and its flags; what it means for the drawing belongs to M8 (11.4.7).
    /// </summary>
    [Fact]
    public void Pages_GroupAttributes_Kept()
    {
        using var saved = Saved(FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Group << /Type /Group /S /Transparency /CS /DeviceRGB /I true /K false >> >>"));
        var group = Assert.IsType<PdfDictionary>(saved.Pages[0].Dictionary.Get(PdfName.Get("Group")));
        Assert.Equal("Transparency", group.GetName(PdfName.Get("S"))?.Value);
        Assert.Equal("DeviceRGB", group.GetName(PdfName.Get("CS"))?.Value);
        Assert.True(Assert.IsType<PdfBoolean>(group.Get(PdfName.Get("I"))).Value);
        Assert.False(Assert.IsType<PdfBoolean>(group.Get(PdfName.Get("K"))).Value);
    }

    /// <summary>
    /// 7.7.3.3, Table 31, /B (sentences 63 and 64): the beads of the page are listed by indirect references in natural reading order — three beads
    /// from the top of the page down; the copy keeps the order (each bead found again by its rectangle) and each bead points back to the page.
    /// </summary>
    [Fact]
    public void Pages_BeadsInReadingOrder_Kept()
    {
        using var saved = Saved(FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Threads [7 0 R] >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /B [4 0 R 5 0 R 6 0 R] >>",
            "<< /Type /Bead /T 7 0 R /N 5 0 R /V 6 0 R /P 3 0 R /R [10 150 190 190] >>",
            "<< /Type /Bead /T 7 0 R /N 6 0 R /V 4 0 R /P 3 0 R /R [10 80 190 140] >>",
            "<< /Type /Bead /T 7 0 R /N 4 0 R /V 5 0 R /P 3 0 R /R [10 10 190 70] >>",
            "<< /Type /Thread /F 4 0 R >>"));
        var page = saved.Pages[0];
        var beads = page.Dictionary.GetArray(PdfName.Get("B"))!;
        Assert.Equal(3, beads.Count);
        Assert.All(beads, b => Assert.IsType<PdfReference>(b));
        var tops = Enumerable.Range(0, 3).Select(i => beads.Get(i)!.AsDictionary()!.GetArray(PdfName.Get("R"))!.GetNumber(3)).ToList();
        Assert.Equal([190d, 140d, 70d], tops);
        Assert.All(Enumerable.Range(0, 3), i => Assert.Equal(page.Id, Assert.IsType<PdfReference>(beads.Get(i)!.AsDictionary()!.GetRaw(PdfName.Get("P"))).Id));
    }

    /// <summary>
    /// 7.7.3.3, Table 31, /B (sentence 65): an object of type Template has no /B. A page template (/Names /Templates) whose source carries one is
    /// written without it — the rest of the template as it stands (docs/FEHLERGEDAECHTNIS.md, No. 1058).
    /// </summary>
    [Fact]
    public void Template_WrittenWithoutBeads()
    {
        using var saved = Saved(FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Threads [6 0 R] /Names << /Templates << /Names [(Vorlage) 4 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /B [5 0 R] >>",
            "<< /Type /Template /MediaBox [0 0 100 50] /Resources << >> /B [5 0 R] /Rotate 90 >>",
            "<< /Type /Bead /T 6 0 R /N 5 0 R /V 5 0 R /P 3 0 R /R [10 10 190 190] >>",
            "<< /Type /Thread /F 5 0 R >>"));
        var names = saved.Catalog!.GetDictionary(PdfName.Get("Names"))!.GetDictionary(PdfName.Get("Templates"))!.GetArray(PdfName.Get("Names"))!;
        var template = names.Get(1)!.AsDictionary()!;
        Assert.Equal("Template", template.GetName(PdfName.Type)?.Value);
        Assert.False(template.ContainsKey(PdfName.Get("B")));
        Assert.Equal(90, template.GetInteger(PdfName.Get("Rotate")));
        Assert.Equal(4, template.GetArray(PdfName.Get("MediaBox"))!.Count);
        Assert.Single(saved.Pages[0].Dictionary.GetArray(PdfName.Get("B"))!);   // the page keeps its bead
    }

    /// <summary>
    /// 7.7.3.3, Table 31, /Tabs (sentences 103 to 105), as a checker: PDF/UA-2 (files of PDF 2.0) takes A (the order of /Annots) and W (widgets
    /// first) besides S as an order of a page with annotations, and reports R, C and none; PDF/UA-1 takes only S (ISO 14289-1, 7.18.3).
    /// </summary>
    [Theory]
    [InlineData("A", false, true)]
    [InlineData("W", false, true)]
    [InlineData("S", false, false)]
    [InlineData("R", true, true)]
    [InlineData("C", true, true)]
    [InlineData(null, true, true)]
    public void Tabs_CheckedForPdfUa2_AAndWTaken(string? tabs, bool foundUa2, bool foundUa1)
    {
        var order = tabs is null ? "" : $" /Tabs /{tabs}";
        using var doc = PdfDocument.Open(FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Version /2.0 /Lang (de) /MarkInfo << /Marked true >> /StructTreeRoot 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << >> /Annots [4 0 R]{order} >>",
            "<< /Type /Annot /Subtype /Text /Rect [100 100 200 130] /F 4 /Contents (Notiz) /P 3 0 R >>",
            "<< /Type /StructTreeRoot /K 6 0 R >>",
            "<< /Type /StructElem /S /Document /P 5 0 R >>"));
        bool Found(AccessibilityProfile profile, string id) => AccessibilityCheck.Check(doc, profile).Findings.Any(f => f.Id == id);
        Assert.Equal(foundUa2, Found(AccessibilityProfile.PdfUa2, "8.9.3.3-1"));
        Assert.Equal(foundUa1, Found(AccessibilityProfile.PdfUa1, "7.18.3-1"));
    }

    /// <summary>
    /// 7.11.3, Table 43 (sentences 29 and 30): a reader takes /UF before /F — the target of a GoToR and of a Launch action shown in a link is the
    /// Unicode name where the specification has one, /F where it has only that.
    /// </summary>
    [Fact]
    public void PageLinks_GoToRAndLaunch_UfBeforeF()
    {
        var unicode = "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("Bericht März.pdf")) + ">";
        using var doc = PdfDocument.Open(FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R 5 0 R 6 0 R] >>",
            $"<< /Type /Annot /Subtype /Link /Rect [10 10 100 30] /A << /S /GoToR /F << /Type /Filespec /F (bericht.pdf) /UF {unicode} >> /D [0 /Fit] >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 40 100 60] /A << /S /Launch /F << /Type /Filespec /F (alt.txt) /UF (neu.txt) >> >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 70 100 90] /A << /S /Launch /F << /Type /Filespec /F (nur-f.txt) >> >> >>"));
        var details = PageLinks.Of(doc, 0).Select(l => Assert.IsType<OtherLink>(l.Target)).Select(t => (t.Kind, t.Detail)).ToList();
        Assert.Equal([("GoToR", "Bericht März.pdf"), ("Launch", "neu.txt"), ("Launch", "nur-f.txt")], details);
    }

    /// <summary>
    /// 7.11.2.1 (sentences 3, 5, 8, 9) as a writer: the string form of a file specification goes out as it was read — a component with a
    /// literal solidus keeps its reverse solidus in /F, and the /UF made from it (or the /F made from a /UF) carries the same text with the
    /// same masking; a platform's bytes stay bytes. PdfKnead never maps the string to a platform name itself (it opens no external file).
    /// </summary>
    [Fact]
    public void FileSpecification_MaskedSolidus_KeptInFAndUf()
    {
        using var saved = Saved(FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R 5 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 10 100 30] /A << /S /GoToR /F << /F (Ordner/in\\\\/out.pdf) >> /D [0 /Fit] >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 40 100 60] /A << /S /GoToR /F << /UF (a\\\\/b.pdf) >> /D [0 /Fit] >> >>"));
        PdfDictionary Spec(int i) => saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(i)!.AsDictionary()!
            .GetDictionary(PdfName.Get("A"))!.GetDictionary(PdfName.Get("F"))!;
        Assert.Equal("Ordner/in\\/out.pdf", Encoding.ASCII.GetString(Spec(0).GetString(PdfName.Get("F"))!.Bytes));
        Assert.Equal("Ordner/in\\/out.pdf", Spec(0).GetString(PdfName.Get("UF"))!.ToText());
        Assert.Equal("a\\/b.pdf", Encoding.ASCII.GetString(Spec(1).GetString(PdfName.Get("F"))!.Bytes));
        Assert.Equal("a\\/b.pdf", Spec(1).GetString(PdfName.Get("UF"))!.ToText());
    }

    /// <summary>
    /// 7.11.4.1, Table 45, /ModDate: PDF/A-3 sets the date of an embedded file where it is missing, otherwise takes it over — a date that is there
    /// stays byte for byte, in the PDF/A-3 copy and in an ordinary one.
    /// </summary>
    [Fact]
    public void EmbeddedFile_ModDatePresent_TakenOver()
    {
        const string date = "D:20200102030405+01'00'";
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "0 0 1 rg 72 600 200 100 re f"u8.ToArray()));
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("EmbeddedFile")),
            ("Params", TestPdf.Dict(("ModDate", Ascii(date)), ("Size", PdfInteger.Of(14))))), "Rechnungsdaten"u8.ToArray()));
        var spec = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Filespec")), ("F", Ascii("daten.xml")), ("UF", Ascii("daten.xml")),
            ("EF", TestPdf.Dict(("F", TestPdf.Ref(file))))));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("Names"),
            TestPdf.Dict(("EmbeddedFiles", TestPdf.Dict(("Names", new PdfArray([Ascii("daten.xml"), TestPdf.Ref(spec)]))))));
        var bytes = pdf.Build();

        static string ModDate(PdfDictionary spec) =>
            Encoding.ASCII.GetString(((PdfStream)spec.GetDictionary(PdfName.Get("EF"))!.Get(PdfName.Get("F"))).Dictionary
                .GetDictionary(PdfName.Get("Params"))!.GetString(PdfName.Get("ModDate"))!.Bytes);

        var three = PdfATests.Write(bytes, PdfAConformance.A3b);
        using (var doc = PdfDocument.Open(three.Bytes))
            Assert.Equal(date, ModDate(Assert.IsType<PdfDictionary>(doc.Catalog!.GetArray(PdfName.Get("AF"))!.Get(0))));
        using var saved = Saved(bytes);
        var names = saved.Catalog!.GetDictionary(PdfName.Get("Names"))!.GetDictionary(PdfName.Get("EmbeddedFiles"))!.GetArray(PdfName.Get("Names"))!;
        Assert.Equal(date, ModDate(names.Get(1)!.AsDictionary()!));
    }

    /// <summary>
    /// 7.9.2.3, sentence 1: a string in PDFDocEncoding holds each character in a single byte — also the characters PDFDocEncoding places apart
    /// from Latin-1 (en dash 85h, euro sign A0h, low and high double quotation marks 8Ch, 8Dh); read back to the same text. Written: a UTF-8 text
    /// string of 2.0 saved as 1.7 becomes exactly these bytes, one per character.
    /// </summary>
    /// <summary>
    /// Annex D, Table D.3: of the control characters PDFDocEncoding has only TAB, LF and CR; 00h–08h, 0Bh, 0Ch, 0Eh–17h and 7Fh have no
    /// character. A text with one of them is written as UTF-16BE (before, as that byte), and a password with one is no PDFDocEncoding password
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1145).
    /// </summary>
    [Theory]
    [InlineData("a\u0001b")]
    [InlineData("a\u000Bb")]
    [InlineData("a\u0017b")]
    [InlineData("a\u007Fb")]
    public void PdfDocEncoding_ControlCharactersWithoutAnEntry_WrittenAsUtf16(string text)
    {
        Assert.Equal([0xFE, 0xFF], PdfTextEncoding.Encode(text)[..2]);
        Assert.Equal(text, PdfTextEncoding.Decode(PdfTextEncoding.Encode(text)));
        Assert.Null(PdfTextEncoding.EncodeDocEncoding(text));
        Assert.Equal(3, PdfTextEncoding.Encode("a\tb").Length);   // TAB, LF and CR stay single bytes
    }

    [Fact]
    public void PdfDocEncoding_OneBytePerCharacter()
    {
        const string text = "Café – 5 € „gut“";
        var bytes = PdfTextEncoding.Encode(text);
        Assert.Equal(text.Length, bytes.Length);
        Assert.Equal((byte)0xE9, bytes[3]);
        Assert.Equal((byte)0x85, bytes[5]);
        Assert.Equal((byte)0xA0, bytes[9]);
        Assert.Equal((byte)0x8C, bytes[11]);
        Assert.Equal((byte)0x8D, bytes[^1]);
        Assert.Equal(text, PdfTextEncoding.Decode(bytes));

        var utf8 = "<EFBBBF" + Convert.ToHexString(Encoding.UTF8.GetBytes(text)) + ">";
        using var old = Saved(FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Version /2.0 >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            $"<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /Contents {utf8} >>"), new PdfSaveOptions { Version = "1.7" });
        var contents = old.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!.AsDictionary()!.GetString(PdfName.Get("Contents"))!;
        Assert.Equal(bytes, contents.Bytes);
    }
}
