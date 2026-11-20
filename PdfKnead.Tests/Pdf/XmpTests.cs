using System.Text;
using System.Xml;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The document's XMP follows each save in every version (docs/ABLAUFPLAN.md, 6.1 step 2; docs/FEHLERGEDAECHTNIS.md, No. 613): date of the
/// change and program as in /Info (ISO 32000-2, 14.3.4), a new instance identifier (XMP part 1, 8.6), the entries of /Info and the XMP brought
/// in line by date (XMP part 3, 2.2.2), the preview removed as XML.
/// </summary>
public sealed class XmpTests
{
    private static readonly DateTimeOffset SavedAt = new(2026, 11, 7, 10, 20, 30, TimeSpan.FromHours(1));
    private const string SavedText = "2026-11-07T10:20:30+01:00";

    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#", Dc = "http://purl.org/dc/elements/1.1/", XmpNs = "http://ns.adobe.com/xap/1.0/",
        PdfNs = "http://ns.adobe.com/pdf/1.3/", Mm = "http://ns.adobe.com/xap/1.0/mm/";

    private const string Namespaces = "xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" " +
                                      "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmpMM=\"http://ns.adobe.com/xap/1.0/mm/\"";

    /// <summary>A one-page file with <paramref name="description"/> as its XMP and <paramref name="info"/> (dictionary body) as /Info.</summary>
    private static byte[] File(string description, string? info = null) => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R/Metadata 5 0 R>>",
        [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R>>",
        [4] = ("", "0 0 m 10 10 l S"),
        [5] = ("/Type/Metadata/Subtype/XML", "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">"
            + $"<rdf:RDF xmlns:rdf=\"{Rdf}\">" + description + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>"),
        [6] = info ?? "<<>>",
    }, info is null ? "" : "/Info 6 0 R");

    private static string Description(string inner, string attributes = "") => $"<rdf:Description rdf:about=\"\" {Namespaces} {attributes}>{inner}</rdf:Description>";

    private static (PdfDocument Doc, PdfSaveResult Result) Save(byte[] file, string version = "1.7", DateTimeOffset? at = null, Func<PdfDocument, PageItem[], PageItem[]>? change = null,
        string? title = null)
    {
        using var doc = PdfDocument.Open(file);
        var items = PageItem.From(doc);
        if (change is not null) items = change(doc, items);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, items, output, new PdfSaveOptions { Version = version, ModificationTime = at ?? SavedAt, ApplyRedactions = true, DocumentTitle = title });
        return (PdfDocument.Open(output.ToArray()), result);
    }

    private static XmlDocument XmpOf(PdfDocument doc) => PdfAXmp.Read(doc.GetStreamData((PdfStream)doc.Catalog!.Get(PdfName.Metadata)))!;

    private static string Text(PdfDocument doc) => Encoding.UTF8.GetString(doc.GetStreamData((PdfStream)doc.Catalog!.Get(PdfName.Metadata)));

    private static string[] Values(XmlDocument xml, string name, string ns) =>
        [.. xml.GetElementsByTagName(name, ns).OfType<XmlElement>().Select(e => e.InnerText.Trim())];

    private static string? Info(PdfDocument doc, string key) => doc.Trailer.GetDictionary(PdfName.Info)?.GetString(PdfName.Get(key))?.ToText();

    /// <summary>
    /// Saved as 1.4 or 1.7, date of change, metadata date and program in the XMP are those of /Info — before, only a 2.0 copy had them
    /// (No. 552); a 1.x copy said in /Info „changed now“ and in the XMP „changed in 2020“. A packet without these properties gets them.
    /// </summary>
    [Theory]
    [InlineData("1.4", true)]
    [InlineData("1.7", true)]
    [InlineData("1.7", false)]
    [InlineData("2.0", true)]
    public void Save_InfoAndXmpModifyDateAreTheSameInstant(string version, bool withDates)
    {
        var file = File(Description(withDates ? "<xmp:ModifyDate>2020-01-02T03:04:05+01:00</xmp:ModifyDate><xmp:MetadataDate>2020-01-02T03:04:05+01:00</xmp:MetadataDate>"
                                                + "<pdf:Producer>Alter Erzeuger</pdf:Producer>" : "<dc:format>application/pdf</dc:format>"));
        var (doc, result) = Save(file, version);
        using (doc)
        {
            Assert.False(result.XmpNotUpdated);
            var xml = XmpOf(doc);
            Assert.Equal([SavedText], Values(xml, "ModifyDate", XmpNs));
            Assert.Equal([SavedText], Values(xml, "MetadataDate", XmpNs));
            Assert.Equal(SavedAt, PdfDate.Parse(Info(doc, "ModDate")));
            var producer = Assert.Single(Values(xml, "Producer", PdfNs));
            Assert.StartsWith("PDF-Knead ", producer);
            // /Info names the program as well — not a 2.0 copy of a source without /Producer (deprecated there, No. 810)
            Assert.Equal(version == "2.0" ? null : producer, Info(doc, "Producer"));
        }
    }

    /// <summary>The values in attribute form: replaced, not doubled (1.7 as well).</summary>
    [Fact]
    public void AttributeForm_Updated()
    {
        var (doc, _) = Save(File(Description("", "xmp:ModifyDate=\"2020-01-02T03:04:05+01:00\" pdf:Producer=\"Alter Erzeuger\"")));
        using (doc)
        {
            var text = Text(doc);
            Assert.DoesNotContain("2020-01-02", text);
            Assert.DoesNotContain("Alter Erzeuger", text);
            Assert.Equal([SavedText], Values(XmpOf(doc), "ModifyDate", XmpNs));
        }
    }

    /// <summary>
    /// The XMP was changed after /Info (its metadata date is the more recent): its title and authors are in force and go into /Info — before,
    /// /Info kept the old title, which readers of 1.x show (XMP part 3, 2.2.2).
    /// </summary>
    [Fact]
    public void XmpNewer_TitleAndAuthorsWin_WrittenToInfo()
    {
        var file = File(Description("<xmp:MetadataDate>2024-05-06T07:08:09Z</xmp:MetadataDate><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Neuer Titel</rdf:li></rdf:Alt></dc:title>"
                                    + "<dc:creator><rdf:Seq><rdf:li>Max Mustermann</rdf:li><rdf:li>Erika Mustermann</rdf:li></rdf:Seq></dc:creator>"),
            "<</Title(Alter Titel)/Author(Niemand)/ModDate(D:20200101000000Z)/Keywords(bleibt)>>");
        var (doc, _) = Save(file);
        using (doc)
        {
            Assert.Equal("Neuer Titel", Info(doc, "Title"));
            Assert.Equal("Max Mustermann; Erika Mustermann", Info(doc, "Author"));
            Assert.Equal("bleibt", Info(doc, "Keywords"));   // the XMP has none: the entry stays
            Assert.Equal(["Max Mustermann", "Erika Mustermann"], Values(XmpOf(doc), "li", Rdf).Where(v => v.Contains("Mustermann")).ToArray());
        }
    }

    /// <summary>
    /// /Info was changed after the XMP (by a program that does not know XMP): its entries are in force and go into the XMP — title, author,
    /// subject, keywords, creating program. Before, the XMP kept the old values.
    /// </summary>
    [Fact]
    public void InfoNewer_EntriesGoIntoTheXmp()
    {
        var file = File(Description("<xmp:MetadataDate>2020-01-01T00:00:00Z</xmp:MetadataDate><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Alter Titel</rdf:li>"
                                    + "<rdf:li xml:lang=\"de\">Alter Titel</rdf:li></rdf:Alt></dc:title><dc:creator><rdf:Seq><rdf:li>Max Mustermann</rdf:li></rdf:Seq></dc:creator>"),
            "<</Title(Neuer Titel)/Author(Erika Mustermann)/Subject(Rechnung Mai)/Keywords(Rechnung; Mai)/Creator(Textprogramm)/ModDate(D:20240506070809Z)>>");
        var (doc, _) = Save(file);
        using (doc)
        {
            var xml = XmpOf(doc);
            Assert.Equal(["Neuer Titel"], Values(xml, "title", Dc));
            Assert.Equal(["Erika Mustermann"], Values(xml, "creator", Dc));
            Assert.Equal(["Rechnung Mai"], Values(xml, "description", Dc));
            Assert.Equal(["Rechnung; Mai"], Values(xml, "Keywords", PdfNs));
            Assert.Equal(["Textprogramm"], Values(xml, "CreatorTool", XmpNs));
            Assert.Equal("Neuer Titel", Info(doc, "Title"));
        }
    }

    /// <summary>A title given for the copy wins over both (protection).</summary>
    [Fact]
    public void GivenTitle_WinsOverBoth()
    {
        var file = File(Description("<xmp:MetadataDate>2024-05-06T07:08:09Z</xmp:MetadataDate><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">XMP-Titel</rdf:li></rdf:Alt></dc:title>"),
            "<</Title(Info-Titel)/ModDate(D:20200101000000Z)>>");
        var (doc, _) = Save(file, title: "Gewählter Titel");
        using (doc)
        {
            Assert.Equal("Gewählter Titel", Info(doc, "Title"));
            Assert.Equal(["Gewählter Titel"], Values(XmpOf(doc), "title", Dc));
        }
    }

    /// <summary>A schema PDF-Knead does not know stays as it is (XMP part 1, 6.1; protection).</summary>
    [Fact]
    public void UnknownSchemaKept()
    {
        var (doc, _) = Save(File(Description("<test:Ablage xmlns:test=\"http://example.org/ns/ablage/\">Ordner 7</test:Ablage>")));
        using (doc) Assert.Equal(["Ordner 7"], Values(XmpOf(doc), "Ablage", "http://example.org/ns/ablage/"));
    }

    /// <summary>
    /// Each save is an instance of its own: a new xmpMM:InstanceID, the document identifier stays (XMP part 1, 8.6) — before, the copy had
    /// the instance identifier of its source. Derived from the time of the save: another time, another identifier; the same, the same.
    /// </summary>
    [Fact]
    public void InstanceId_NewEachSave_DocumentIdStays()
    {
        var file = File(Description("<xmpMM:DocumentID>uuid:11111111-2222-3333-4444-555555555555</xmpMM:DocumentID>"
                                    + "<xmpMM:InstanceID>uuid:66666666-7777-8888-9999-000000000000</xmpMM:InstanceID>"));
        string Instance(DateTimeOffset at)
        {
            var (doc, _) = Save(file, at: at);
            using (doc)
            {
                var xml = XmpOf(doc);
                Assert.Equal(["uuid:11111111-2222-3333-4444-555555555555"], Values(xml, "DocumentID", Mm));
                return Assert.Single(Values(xml, "InstanceID", Mm));
            }
        }
        var first = Instance(SavedAt);
        Assert.NotEqual("uuid:66666666-7777-8888-9999-000000000000", first);
        Assert.Matches("^uuid:[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", first);
        Assert.Equal(first, Instance(SavedAt));
        Assert.NotEqual(first, Instance(SavedAt.AddSeconds(1)));
    }

    /// <summary>
    /// The preview under the older prefixes (xap:Thumbnails, an image xapGImg:image outside it) goes with a bar — read as XML, the prefix does
    /// not matter. Before, the regular expression knew only the prefix xmpGImg and left the lone image of the page as it was.
    /// </summary>
    [Fact]
    public void Redaction_RemovesPreviewsUnderOlderPrefixes()
    {
        var file = File("<rdf:Description rdf:about=\"\" xmlns:xap=\"http://ns.adobe.com/xap/1.0/\" xmlns:xapGImg=\"http://ns.adobe.com/xap/1.0/g/img/\" " +
                        "xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Titel bleibt</rdf:li></rdf:Alt></dc:title>" +
                        "<xap:Thumbnails><rdf:Alt><rdf:li rdf:parseType=\"Resource\"><xapGImg:image>VORSCHAU1</xapGImg:image></rdf:li></rdf:Alt></xap:Thumbnails></rdf:Description>" +
                        "<rdf:Description rdf:about=\"\" xmlns:xapGImg=\"http://ns.adobe.com/xap/1.0/g/img/\"><xapGImg:image>VORSCHAU2</xapGImg:image></rdf:Description>");
        var (kept, _) = Save(file);
        using (kept) Assert.Contains("VORSCHAU2", Text(kept));
        var (doc, _) = Save(file, change: (_, items) =>
        {
            items[0] = items[0].WithElements([new RedactionElement { Rect = new PdfRectangle(0, 0, 20, 20) }]);
            return items;
        });
        using (doc)
        {
            var text = Text(doc);
            Assert.DoesNotContain("VORSCHAU", text);
            Assert.DoesNotContain("Thumbnails", text);
            Assert.Contains("Titel bleibt", text);
        }
    }

    /// <summary>
    /// A PDF/A-1b file saved the ordinary way stays consistent: each entry of /Info equal to its XMP property (ISO 19005-1, 6.7.3). Before, the
    /// new /ModDate and /Producer stood beside the old XMP values — the copy no longer was what it claimed.
    /// </summary>
    [Fact]
    public void PdfA1b_SavedTheOrdinaryWay_InfoStillMatchesXmp()
    {
        var pdfA = PdfATests.Write(TestPdf.Simple(1, title: "Bericht").Build(), PdfAConformance.A1b);
        Assert.True(pdfA.Conforms, PdfATests.Describe(pdfA.Violations));
        var (doc, result) = Save(pdfA.Bytes, version: "1.4");
        using (doc)
        {
            Assert.Null(result.PdfAClaimRemovedFor);
            Assert.Equal(PdfAConformance.A1b, PdfACheck.Declared(doc));
            Assert.DoesNotContain(PdfACheck.Check(doc), v => v.Id.StartsWith("6.7.3", StringComparison.Ordinal));
        }
    }

    /// <summary>An XMP that is no well-formed XML stays as it is in 1.7 as well, and that is said (as in 2.0).</summary>
    [Fact]
    public void UnreadableXmp_StaysAndIsSaid()
    {
        var (doc, result) = Save(File("<rdf:Description rdf:about=\"\"><kaputt></rdf:Description>"));
        using (doc)
        {
            Assert.True(result.XmpNotUpdated);
            Assert.Contains("<kaputt>", Text(doc));
        }
    }
}
