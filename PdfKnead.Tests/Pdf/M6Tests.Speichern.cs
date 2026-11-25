using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Graphics and colour (ISO 32000-2, chapter 8; Konformitätsabgleich M6): saving writes what the target version allows — repairs that keep
/// the meaning, in new or rewritten content only where a signature covers the file (docs/FEHLERGEDAECHTNIS.md, No. 1131).
/// </summary>
public sealed partial class M6NormTests
{
    // ---- helpers

    /// <summary>The file saved with the options given, as bytes.</summary>
    private static byte[] E3_Save(byte[] file, PdfSaveOptions? options = null)
    {
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, options ?? new PdfSaveOptions());
        return output.ToArray();
    }

    /// <summary>Every page of the copy looks like the same page of the original, pixel for pixel.</summary>
    private static void E3_LooksTheSame(byte[] before, byte[] after) => E3_LooksAlike(before, after, 0);

    /// <summary>Every page of the copy looks like the same page of the original, each channel of each pixel within <paramref name="tolerance"/>.</summary>
    private static void E3_LooksAlike(byte[] before, byte[] after, int tolerance)
    {
        using var a = PdfDocument.Open(before);
        using var b = PdfDocument.Open(after);
        Assert.Equal(a.PageCount, b.PageCount);
        for (var i = 0; i < a.PageCount; i++)
        {
            using var pa = ContentImageTests.Render(a, a.Pages[i]);
            using var pb = ContentImageTests.Render(b, b.Pages[i]);
            Assert.Equal((pa.Width, pa.Height), (pb.Width, pb.Height));
            var differing = 0;
            for (var y = 0; y < pa.Height; y++)
                for (var x = 0; x < pa.Width; x++)
                    if (pa.GetPixel(x, y) is var p && pb.GetPixel(x, y) is var q &&
                        (Math.Abs(p.Red - q.Red) > tolerance || Math.Abs(p.Green - q.Green) > tolerance || Math.Abs(p.Blue - q.Blue) > tolerance ||
                         Math.Abs(p.Alpha - q.Alpha) > tolerance))
                        differing++;
            Assert.True(differing == 0, $"S. {i + 1}: {differing} Bildpunkte anders");
        }
    }

    /// <summary>The operators of the first page's content as the parser reads them, with their operands as written.</summary>
    private static List<string> E3_Operators(PdfDocument doc)
    {
        var data = Encoding.Latin1.GetBytes(I89_Content(doc));
        var parser = new ContentParser(data);
        var result = new List<string>();
        while (parser.Next() is { } op)
            result.Add(string.Join(" ", parser.Operands.Select(o => o.Object is null ? o.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : Encoding.Latin1.GetString(PdfSerializer.ToBytes(o.Object))).Append(op)));
        return result;
    }

    /// <summary>A stream object with the entries given and its content (its /Length counted).</summary>
    private static string E3_Stream(string entries, string content) => $"<< {entries} /Length {content.Length} >>\nstream\n{content}\nendstream";

    /// <summary>Every stream of a document whose dictionary has /Subtype /Form.</summary>
    private static List<PdfStream> E3_Forms(PdfDocument doc) =>
        [.. Enumerable.Range(1, doc.HighestObjectNumber).Select(n => doc.GetObject(n)).OfType<PdfStream>()
            .Where(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Form")];

    // ---- 8.9.7: /L and size of inline images (questions 3 and 4)

    /// <summary>
    /// 8.9.7 sentence 24: in PDF 2.0 every inline image states the length of its data (/L). Saved as 2.0, an inline image without /L gets it,
    /// one with a wrong /L the right one, and the data stay byte for byte (a grey sample 0x20, which looks like white-space, too); the page looks
    /// the same. Saved as 1.7 the content stays as it was. Before, /L was added only for PDF/A-4 (docs/FEHLERGEDAECHTNIS.md, No. 1131).
    /// </summary>
    [Fact]
    public void InlineImage_SavedAs20_LengthAdded_DataByteForByte()
    {
        const string content = "q 100 0 0 200 0 0 cm BI /W 2 /H 1 /CS /G /BPC 8 ID \x80\x20 EI Q q 100 0 0 200 100 0 cm BI /W 1 /H 1 /CS /G /BPC 8 /L 7 ID \x40 EI Q";
        var file = I89_File("", "", content);
        var as20 = E3_Save(file, new PdfSaveOptions { Version = "2.0" });
        E3_LooksTheSame(file, as20);
        using (var saved = PdfDocument.Open(as20))
        {
            var written = I89_Content(saved);
            var parser = new ContentParser(Encoding.Latin1.GetBytes(written));
            var images = new List<InlineImage>();
            while (parser.Next() is { } op)
                if (op == "BI" && parser.InlineImage is { } image) images.Add(image);
            Assert.Equal(2, images.Count);
            Assert.Equal(new byte[] { 0x80, 0x20 }, images[0].Data);
            Assert.Equal(2, images[0].Dictionary.GetInteger(PdfName.Get("L")));
            Assert.Equal(new byte[] { 0x40 }, images[1].Data);
            Assert.Equal(1, images[1].Dictionary.GetInteger(PdfName.Get("L")));
            // the key written once, as its abbreviation
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(written, @"/L\s").Count);
            Assert.DoesNotContain("/Length", written);
        }
        using var as17 = DocumentStructureNormTests.Saved(file, new PdfSaveOptions { Version = "1.7" });
        Assert.Equal(content, I89_Content(as17).Trim());
    }

    // ---- 8.9.5.1, 8.10.2: /OPI (question 5)

    /// <summary>
    /// 8.9.5.1 sentences 82–85 and 8.10.2 sentences 62–65: /OPI of images and forms is deprecated in PDF 2.0 — saved as 2.0 both go without
    /// it, saved as 1.7 with it (<c>VersionKeys.Filter</c>, as for /Name); the page looks the same.
    /// </summary>
    [Fact]
    public void Opi_OfImageAndForm_GoesAs20_StaysAs17()
    {
        const string opi = "/OPI << /1.3 << /Type /OPI /Version 1.3 /F (bild.tif) /Size [1 1] /CropRect [0 0 1 1] /Position [0 0 1 0 1 1 0 1] >> >>";
        var file = I89_File("", "/XObject << /Im0 5 0 R /Fm0 6 0 R >>", "q 100 0 0 200 0 0 cm /Im0 Do Q /Fm0 Do",
            I89_Image($"{opi} /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8", "\xff\x00\x00"),
            J810_Form($"{opi} /BBox [100 0 200 200] /Resources << >>", "0 0 1 rg 100 0 100 200 re f"));
        var as20 = E3_Save(file, new PdfSaveOptions { Version = "2.0" });
        E3_LooksTheSame(file, as20);
        using (var saved = PdfDocument.Open(as20))
        {
            Assert.Null(Assert.Single(I89_Images(saved)).Dictionary.GetRaw(PdfName.Get("OPI")));
            Assert.Null(Assert.Single(J810_XObjects(saved, "Form")).Dictionary.GetRaw(PdfName.Get("OPI")));
        }
        using var as17 = DocumentStructureNormTests.Saved(file, new PdfSaveOptions { Version = "1.7" });
        Assert.NotNull(Assert.Single(I89_Images(as17)).Dictionary.GetDictionary(PdfName.Get("OPI")));
        Assert.NotNull(Assert.Single(J810_XObjects(as17, "Form")).Dictionary.GetDictionary(PdfName.Get("OPI")));
    }

    // ---- question 6: repairs that keep the meaning

    /// <summary>
    /// 8.5.3.1 sentences 19–22: F is obsolete, writers use f. Foreign content with F is saved with f, as 1.7 and as 2.0 — an F inside a string
    /// or a name stays; the page looks the same.
    /// </summary>
    [Theory]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public void C85_CapitalF_InForeignContent_WrittenAsF(string version)
    {
        var file = I89_File("", "/ExtGState << /F << /CA 1 >> >> /Font << /F1 5 0 R >>",
            "/F gs 0 0 1 rg 20 20 60 60 re F 1 0 0 rg 100 100 60 60 re\nF BT /F1 12 Tf 20 180 Td (F) Tj ET",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var saved = E3_Save(file, new PdfSaveOptions { Version = version });
        E3_LooksTheSame(file, saved);
        using var doc = PdfDocument.Open(saved);
        var operators = E3_Operators(doc);
        Assert.DoesNotContain(operators, o => o == "F" || o.EndsWith(" F", StringComparison.Ordinal));
        Assert.Equal(2, operators.Count(o => o == "f"));
        Assert.Contains("/F gs", operators);
        Assert.Contains("(F) Tj", operators);
    }

    /// <summary>
    /// 8.4.3.6 sentence 11: an empty dash array goes with phase 0. Foreign content with „[] 3 d“ is saved with „[] 0 d“, as 1.7 and as 2.0 —
    /// the line is solid either way; a dash array with elements keeps its phase.
    /// </summary>
    [Theory]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public void B84_EmptyDashArrayWithPhase_WrittenWithPhaseZero(string version)
    {
        var file = I89_File("", "", "0 0 1 RG 4 w [10 10] 0 d [ ] 3 d 20 50 m 180 50 l S [10 10] 5 d 20 150 m 180 150 l S");
        var saved = E3_Save(file, new PdfSaveOptions { Version = version });
        E3_LooksTheSame(file, saved);
        using var doc = PdfDocument.Open(saved);
        var dashes = E3_Operators(doc).Where(o => o.EndsWith(" d", StringComparison.Ordinal)).ToList();
        Assert.Equal(["[10 10] 0 d", "[] 0 d", "[10 10] 5 d"], dashes);
    }

    /// <summary>
    /// 8.10.2 sentences 4–6: a form's /Type, where given, is XObject. A foreign form with /Type /Pattern is saved with /Type /XObject, as 1.7 and
    /// as 2.0; one without resources of its own and with the deprecated /Name still gets those of its page (the dictionary changed for the
    /// version is the same form — before, a form with /Name saved as 2.0 lost the resources it was lent); the page looks the same.
    /// </summary>
    [Theory]
    [InlineData("1.7", "Pattern")]
    [InlineData("2.0", "Pattern")]
    [InlineData("2.0", "XObject")]
    public void Form_WrongType_WrittenAsXObject_KeepsItsLentResources(string version, string type)
    {
        var file = J810_File("/XObject << /Fm0 5 0 R >> /Font << /F1 6 0 R >>", "/Fm0 Do",
            E3_Stream($"/Type /{type} /Subtype /Form /Name /Fm0 /BBox [0 0 200 200]", "1 0 0 rg 0 0 50 50 re f BT /F1 24 Tf 60 100 Td (Form) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var saved = E3_Save(file, new PdfSaveOptions { Version = version });
        E3_LooksTheSame(file, saved);
        using var doc = PdfDocument.Open(saved);
        var form = Assert.Single(J810_XObjects(doc, "Form"));
        Assert.Equal("XObject", form.Dictionary.GetName(PdfName.Type)?.Value);
        Assert.Equal("Helvetica", form.Dictionary.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("Font"))?.GetDictionary(PdfName.Get("F1"))
            ?.GetName(PdfName.Get("BaseFont"))?.Value);
    }

    // ---- signed files: the signed bytes stay, repairs only in what is new

    private static readonly Lazy<SigningCertificate> E3_Own = new(() =>
        SigningCertificates.CreateOwn("Erika Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"m6-speichern-{Guid.NewGuid():N}.p12")));

    /// <summary>
    /// A signed form whose untouched push button has a foreign appearance with F, „[] 3 d“ and an inline image without /L: a filled-in field
    /// is appended (KONZEPT.md, 5.17), the signed bytes stay as they are and the signature valid — and the push button is not appended with a
    /// repaired appearance: the repairs of saving go only into what the update writes anew (docs/FEHLERGEDAECHTNIS.md, No. 1131).
    /// </summary>
    [Fact]
    public void SignedFile_FilledFieldAppended_ForeignAppearanceNotRepairedIntoTheUpdate()
    {
        static PdfString S(string text) => new(PdfTextEncoding.Encode(text));
        static PdfDictionary Widget(double l, double b, double r, double t, params (string Key, PdfObject Value)[] more) =>
            TestPdf.Dict([("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(l, b, r, t)), ("F", PdfInteger.Of(4)), .. more]);

        var pdf = TestPdf.Simple(pages: 1);
        pdf.Replace(1, TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(2)), ("Version", TestPdf.N("2.0"))));
        const int page = 5;
        var look = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 100, 20)),
            ("Resources", new PdfDictionary())), "0 0 1 rg 0 0 100 20 re F [] 3 d q 20 0 0 20 0 0 cm BI /W 1 /H 1 /CS /G /BPC 8 ID \x80 EI Q"u8.ToArray()));
        var button = pdf.Add(Widget(72, 600, 172, 620, ("FT", TestPdf.N("Btn")), ("Ff", PdfInteger.Of(65536)), ("T", S("Stempel")),
            ("AP", TestPdf.Dict(("N", TestPdf.Ref(look)))), ("P", TestPdf.Ref(page))));
        var name = pdf.Add(Widget(72, 700, 272, 720, ("FT", TestPdf.N("Tx")), ("T", S("Name")), ("DA", S("/Helv 12 Tf 0 g")), ("P", TestPdf.Ref(page))));
        ((PdfDictionary)pdf.Get(page)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(button), TestPdf.Ref(name)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(
            ("Fields", new PdfArray([TestPdf.Ref(button), TestPdf.Ref(name)])), ("DA", S("/Helv 0 Tf 0 g"))));
        byte[] signed;
        using (var unsigned = PdfDocument.Open(pdf.Build()))
            signed = PdfSigner.Sign(unsigned, 0, new InkElement
            {
                Rect = new PdfRectangle(350, 100, 530, 160),
                Strokes = [[0, 20, 40, 50, 90, 10, 140, 45, 175, 25]],
                StrokeWidth = 3,
                Digital = new DigitalSigning("Erika Mustermann · 25.11.2026"),
            }, E3_Own.Value, new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.FromHours(1)));

        using var doc = PdfDocument.Open(signed);
        var saved = PageItem.From(doc);
        var filled = saved[0].WithElements([.. saved[0].Elements.Select(e => e is ForeignAnnotation f && FormField.Of(f)?.Name == "Name" ? f with { Value = "Erika Mustermann" } : e)]);
        Assert.True(AppendedChanges.CanAppend(doc, saved, [filled]));
        var appended = AppendedChanges.Apply(doc, [filled], new PdfSaveOptions { Version = "2.0" });
        Assert.Equal(signed, appended.AsSpan(0, signed.Length).ToArray());

        using var reopened = PdfDocument.Open(appended);
        var check = SignatureVerifier.Verify(reopened, null, [], CancellationToken.None).Single();
        Assert.Equal(SignatureOutcome.ValidIdentityUnknown, check.Outcome);
        // the push button and its appearance are the objects of the signed revision: nothing of them in the update
        var buttonAfter = reopened.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Select(r => r).OfType<PdfReference>()
            .First(r => (reopened.Resolve(r.Id) as PdfDictionary)?.GetString(PdfName.Get("T"))?.ToText() == "Stempel");
        Assert.Equal(button, buttonAfter.Id.Number);
        var appearance = (reopened.Resolve(buttonAfter.Id) as PdfDictionary)!.GetDictionary(PdfName.Get("AP"))!.GetRaw(PdfName.N) as PdfReference;
        Assert.Equal(look, appearance?.Id.Number);
        Assert.Contains("re F [] 3 d", Encoding.Latin1.GetString(reopened.GetStreamData((PdfStream)reopened.Resolve(appearance!.Id)!)));
        Assert.Equal("Erika Mustermann", FormField.Of(PageItem.From(reopened)[0].Elements.OfType<ForeignAnnotation>().First(f => FormField.Of(f)?.Name == "Name"))!.StoredValue);
    }

    // ---- 8.10.2 sentences 30–32 (V10): forms never drawn without resources of their own (question 7)

    /// <summary>
    /// 8.10.2 sentences 30–32, 7.8.3 (V10): a form without resources of its own that no content draws gets the resources of the first page
    /// whose /XObject names it — the names its content uses (page 1's Helvetica, not page 2's Courier). A form that no page names (only the
    /// /XObject of another form, which never draws it) stays without and is reported (<c>PdfSaveResult.FormsWithoutResources</c>); both
    /// pages look the same. (Page 1 has a part of its content that is no stream: which resources it uses is not known, they are kept whole.)
    /// </summary>
    [Theory]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public void Form_NeverDrawnWithoutResources_GetsThoseOfTheFirstPageNamingIt(string version)
    {
        var file = DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 7 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Fm0 5 0 R /Fm1 6 0 R >> /Font << /F1 8 0 R >> >> /Contents [4 0 R 9 0 R] >>",
            E3_Stream("", "/Fm0 Do"),
            J810_Form("/BBox [0 0 200 200] /Resources << /XObject << /Fm2 10 0 R >> >>", "0 0 1 rg 0 0 50 50 re f"),
            E3_Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 200]", "BT /F1 12 Tf 10 10 Td (Eins) Tj ET"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Fm1 6 0 R >> /Font << /F1 11 0 R >> >> /Contents 12 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Kind /NoStream >>",
            E3_Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 200]", "BT /F2 12 Tf 10 10 Td (Zwei) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>",
            E3_Stream("", "1 0 0 rg 0 0 20 20 re f"));
        using var original = PdfDocument.Open(file);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(original, PageItem.From(original), output, new PdfSaveOptions { Version = version });
        E3_LooksTheSame(file, output.ToArray());
        using var doc = PdfDocument.Open(output.ToArray());
        string Text(PdfStream s) => Encoding.Latin1.GetString(doc.GetStreamData(s));
        var eins = Assert.Single(E3_Forms(doc), f => Text(f).Contains("(Eins)", StringComparison.Ordinal));
        Assert.Equal("Helvetica", eins.Dictionary.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("Font"))?.GetDictionary(PdfName.Get("F1"))
            ?.GetName(PdfName.Get("BaseFont"))?.Value);
        // only what it uses: not the forms of the page
        Assert.Null(eins.Dictionary.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("XObject")));
        var zwei = Assert.Single(E3_Forms(doc), f => Text(f).Contains("(Zwei)", StringComparison.Ordinal));
        Assert.Null(zwei.Dictionary.GetRaw(PdfName.Resources));
        Assert.Equal(1, result.FormsWithoutResources);
    }

    // ---- the PDF/A copy and Type 3 glyphs

    /// <summary>The PDF/A copy of a file: it conforms, and the check of the copy read anew finds nothing.</summary>
    private static byte[] E3_PdfA(byte[] file, PdfAConformance part)
    {
        using var doc = PdfDocument.Open(file);
        var copy = PdfAExport.Write(doc, PageItem.From(doc), part);
        Assert.True(copy.Conforms, PdfATests.Describe(copy.Violations));
        using var written = PdfDocument.Open(copy.Bytes);
        Assert.Empty(PdfACheck.Check(written, part));
        return copy.Bytes;
    }

    /// <summary>
    /// 8.9.7 sentences 3 and 25 in the PDF/A-4 copy (based on 2.0): an inline image of more than 4 096 bytes as written becomes an image
    /// XObject of the page, and goes the way of every image of the copy — grey data as they are; data with /Crypt (not allowed in PDF/A) as
    /// the display decodes them; RGB data as hexadecimal digits (6 001 bytes written, 3 000 read) without the ASCII filter. The copy conforms,
    /// the check of the copy finds nothing, the page looks the same; an image of 4 096 bytes stays inline (docs/FEHLERGEDAECHTNIS.md, No. 1131).
    /// </summary>
    [Theory]
    [InlineData("/W 100 /H 50 /CS /G /BPC 8", "grau")]
    [InlineData("/W 100 /H 60 /CS /G /BPC 8 /F /Crypt", "crypt")]
    [InlineData("/W 50 /H 20 /CS /RGB /BPC 8 /F /AHx", "hex")]
    public void InlineImage_Over4096Bytes_PdfA4Copy_BecomesAnImageXObject(string entries, string kind)
    {
        // (the data with /Crypt are repacked with Flate: samples that Flate does not shrink below 4 096 bytes, without „EI“)
        var random = new Random(1131);
        var data = kind switch
        {
            "hex" => Convert.ToHexString([.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 7 % 251))]) + ">",
            "crypt" => string.Concat(Enumerable.Range(0, 6000).Select(_ => (char)random.Next(0x80, 0x100))),
            _ => string.Concat(Enumerable.Range(0, 5000).Select(i => (char)(0x40 + i % 128))),
        };
        var small = new string('\x80', 4096);
        var file = PdfARuleCases.Raw(PdfARuleCases.Page(
            $"q 200 0 0 100 72 600 cm BI {entries} ID {data} EI Q q 64 0 0 64 72 400 cm BI /W 64 /H 64 /CS /G /BPC 8 ID {small} EI Q"));
        var copy = E3_PdfA(file, PdfAConformance.A4);
        E3_LooksAlike(file, copy, 2);
        using var doc = PdfDocument.Open(copy);
        var content = I89_Content(doc);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(content, @"\bBI\b"));
        Assert.Contains("/L 4096", content);
        var image = Assert.Single(I89_Images(doc));
        Assert.DoesNotContain(StreamFiltersOf(image), f => f is "Crypt" or "ASCIIHexDecode" or "AHx");
    }

    private static List<string> StreamFiltersOf(PdfStream stream) => stream.Dictionary.Get(PdfName.Filter) switch
    {
        PdfName name => [name.Value],
        PdfArray array => [.. Enumerable.Range(0, array.Count).Select(i => (array[i] as PdfName)?.Value ?? "")],
        _ => [],
    };

    /// <summary>
    /// The same in a form of the page: the image XObject goes into the resources the form draws with, now its own (PDF/A-4).
    /// </summary>
    [Fact]
    public void InlineImage_Over4096Bytes_InAForm_PdfA4Copy_BecomesAnImageXObjectOfTheForm()
    {
        var data = string.Concat(Enumerable.Range(0, 5000).Select(i => (char)(0x40 + i % 128)));
        var file = PdfARuleCases.Raw(PdfARuleCases.Page("q 1 0 0 1 72 500 cm /Fm1 Do Q", resources: "/XObject<</Fm1 5 0 R>>",
            more: PdfARuleCases.Stream(5, "/Type/XObject/Subtype/Form/BBox[0 0 200 100]/Resources<<>>",
                $"q 200 0 0 100 0 0 cm BI /W 100 /H 50 /CS /G /BPC 8 ID {data} EI Q")));
        var copy = E3_PdfA(file, PdfAConformance.A4);
        E3_LooksAlike(file, copy, 2);
        using var doc = PdfDocument.Open(copy);
        var form = Assert.Single(J810_XObjects(doc, "Form"));
        Assert.DoesNotContain("BI", Encoding.Latin1.GetString(doc.GetStreamData(form)));
        var xobjects = form.Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("XObject"))!;
        Assert.Single(xobjects.Keys, k => xobjects.Get(k) is PdfStream s && s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image");
    }

    /// <summary>
    /// A one-page file with a Type 3 font /T3 whose glyph „a“ draws an inline image of 5 000 bytes over its whole cell; the font's
    /// /Resources (object 9) are shared with a form of the page, which draws a red square with them.
    /// </summary>
    private static byte[] E3_Type3File(bool direct = false)
    {
        var data = string.Concat(Enumerable.Range(0, 5000).Select(i => (char)(0x40 + i % 128)));
        const string font = "<</Type/Font/Subtype/Type3/FontBBox[0 0 1000 1000]/FontMatrix[0.001 0 0 0.001 0 0]/CharProcs 6 0 R" +
                            "/Encoding<</Type/Encoding/Differences[97/a]>>/FirstChar 97/LastChar 97/Widths[1000]/Resources 9 0 R>>";
        return PdfARuleCases.Raw(PdfARuleCases.Page("BT /T3 100 Tf 72 600 Td (a) Tj ET q 1 0 0 1 300 600 cm /Fm1 Do Q",
            resources: $"/Font<</T3 {(direct ? font : "5 0 R")}>>/XObject<</Fm1 8 0 R>>",
            more:
            [
                PdfARuleCases.Obj(5, direct ? "<<>>" : font),
                PdfARuleCases.Obj(6, "<</a 7 0 R>>"),
                PdfARuleCases.Stream(7, "", $"1000 0 d0 q 1000 0 0 1000 0 0 cm BI /W 100 /H 50 /CS /G /BPC 8 ID {data} EI Q"),
                PdfARuleCases.Stream(8, "/Type/XObject/Subtype/Form/BBox[0 0 100 100]/Resources 9 0 R", "/GS1 gs 1 0 0 rg 0 0 100 100 re f"),
                PdfARuleCases.Obj(9, "<</ExtGState<</GS1<</Type/ExtGState/CA 1>>>>>>"),
            ]));
    }

    /// <summary>The glyph procedure „a“ of the Type 3 font of the first page, its font, and the form /Fm1.</summary>
    private static (PdfStream Glyph, PdfDictionary Font, PdfStream Form) E3_Type3Parts(PdfDocument doc)
    {
        var font = doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("T3"))!;
        var glyph = (PdfStream)font.GetDictionary(PdfName.Get("CharProcs"))!.Get(PdfName.Get("a"));
        return (glyph, font, Assert.Single(J810_XObjects(doc, "Form")));
    }

    /// <summary>
    /// 8.9.7 sentences 3 and 25 in glyph procedures of a Type 3 font: saved as 2.0 the inline image of 5 000 bytes becomes an image XObject in
    /// the font's /Resources — a copy: the form that shares the resources object does not get it —, the glyph calls it; the page looks the
    /// same. Saved as 1.7 the glyph keeps its inline image (docs/FEHLERGEDAECHTNIS.md, No. 1131; before, glyphs stayed as they were, as in
    /// No. 993).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Type3Glyph_InlineImageOver4096Bytes_As20_AnImageXObjectOfTheFont(bool direct)
    {
        var file = E3_Type3File(direct);
        var as20 = E3_Save(file, new PdfSaveOptions { Version = "2.0" });
        E3_LooksTheSame(file, as20);
        using (var doc = PdfDocument.Open(as20))
        {
            var (glyph, font, form) = E3_Type3Parts(doc);
            var glyphContent = Encoding.Latin1.GetString(doc.GetStreamData(glyph));
            Assert.DoesNotContain("BI", glyphContent);
            var name = System.Text.RegularExpressions.Regex.Match(glyphContent, @"/(\S+) Do").Groups[1].Value;
            var resources = font.GetDictionary(PdfName.Resources)!;
            var image = (PdfStream)resources.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get(name));
            Assert.Equal(5000, doc.DecodeStream(image).Data.Length);
            Assert.NotNull(resources.GetDictionary(PdfName.Get("ExtGState"))?.GetDictionary(PdfName.Get("GS1")));
            Assert.Null(form.Dictionary.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("XObject")));
        }
        using var as17 = DocumentStructureNormTests.Saved(file, new PdfSaveOptions { Version = "1.7" });
        Assert.Contains("BI", Encoding.Latin1.GetString(as17.GetStreamData(E3_Type3Parts(as17).Glyph)));
    }

    /// <summary>
    /// The same in the PDF/A-4 copy, for a font that is an object of its own and one given directly in the page's /Font (which the copy writes
    /// anew from what it was copied from): the glyph's inline image becomes an image XObject in the font's resources (a copy, not the form's);
    /// the copy conforms, the check of the copy finds nothing, the page looks the same.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Type3Glyph_InlineImageOver4096Bytes_PdfA4Copy_AnImageXObjectOfTheFont(bool direct)
    {
        var file = E3_Type3File(direct);
        var copy = E3_PdfA(file, PdfAConformance.A4);
        E3_LooksAlike(file, copy, 2);
        using var doc = PdfDocument.Open(copy);
        var (glyph, font, form) = E3_Type3Parts(doc);
        var glyphContent = Encoding.Latin1.GetString(doc.GetStreamData(glyph));
        Assert.DoesNotContain("BI", glyphContent);
        var name = System.Text.RegularExpressions.Regex.Match(glyphContent, @"/(\S+) Do").Groups[1].Value;
        var resources = font.GetDictionary(PdfName.Resources)!;
        Assert.IsType<PdfStream>(resources.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get(name)));
        Assert.NotNull(resources.GetDictionary(PdfName.Get("ExtGState"))?.GetDictionary(PdfName.Get("GS1")));
        Assert.Null(form.Dictionary.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("XObject")));
    }

    /// <summary>
    /// 8.10.2 sentences 30–32 (V10) in the PDF/A copy: a form without resources of its own that no content draws gets the resources of the page
    /// that names it — the names its content uses (the graphics state, not the page's other forms); the copy conforms.
    /// </summary>
    [Theory]
    [InlineData(PdfAConformance.A2b)]
    [InlineData(PdfAConformance.A4)]
    public void Form_NeverDrawnWithoutResources_PdfACopy_GetsThoseOfThePage(PdfAConformance part)
    {
        var file = PdfARuleCases.Raw(PdfARuleCases.Page("q 1 0 0 rg 72 600 100 100 re f Q", resources: "/XObject<</Fm1 5 0 R>>/ExtGState<</GS1 6 0 R>>",
            more:
            [
                PdfARuleCases.Stream(5, "/Type/XObject/Subtype/Form/BBox[0 0 100 100]", "/GS1 gs 0 0 1 rg 0 0 100 100 re f"),
                PdfARuleCases.Obj(6, "<</Type/ExtGState/CA 1>>"),
            ]));
        var copy = E3_PdfA(file, part);
        using var doc = PdfDocument.Open(copy);
        var form = Assert.Single(E3_Forms(doc), f => Encoding.Latin1.GetString(doc.GetStreamData(f)).Contains("/GS1 gs", StringComparison.Ordinal));
        var resources = form.Dictionary.GetDictionary(PdfName.Resources);
        Assert.NotNull(resources?.GetDictionary(PdfName.Get("ExtGState"))?.GetDictionary(PdfName.Get("GS1")));
        Assert.Null(resources!.GetDictionary(PdfName.Get("XObject")));
    }
}
