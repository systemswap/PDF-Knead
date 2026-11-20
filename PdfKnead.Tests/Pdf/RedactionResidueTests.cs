using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Filters.Jbig2;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// What redaction must take along (docs/RECHERCHE-VERSTECKEN.md, B-10): the expansion of an abbreviation (/E) of marked content under a bar —
/// written in the content, as named properties, and in the structure tree —, and the shared data of a JBIG2 image (/JBIG2Globals) that no image
/// uses any more after redaction.
/// </summary>
public sealed class RedactionResidueTests
{
    private const string Font = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>";

    private static PdfDocument Open(Dictionary<int, object> objects) => PdfDocument.Open(CraftedFileTests.Build(objects));

    /// <summary>The file with the areas redacted, as text: the file itself and every stream decoded.</summary>
    private static string Redacted(PdfDocument doc, params PdfRectangle[] areas)
    {
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([.. items[0].Elements, RedactionElement.FromPageAreas(areas)]);   // the fields of the page stay elements
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true, CompressStreams = false, ObjectStreams = false });
        using var saved = PdfDocument.Open(output.ToArray());
        var text = new StringBuilder(Encoding.Latin1.GetString(output.ToArray()));
        for (var n = 1; n < (int)(saved.Trailer.GetInteger(PdfName.Size) ?? 0); n++)
            if (saved.GetObject(n) is PdfStream s)
                text.Append(Encoding.Latin1.GetString(saved.GetStreamData(s)));
        return text.ToString();
    }

    private static readonly PdfRectangle OverTheName = new(60, 690, 300, 720);

    [Fact]
    public void Expansion_InTheContent_Goes()
    {
        using var doc = Open(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = Font,
            [4] = "<</Type/Page/Parent 2 0 R/Contents 7 0 R/Resources<</Font<</F1 3 0 R>>>>>>",
            [7] = ("", "/Span <</E (AUSGESCHRIEBENGEHEIM)>> BDC BT /F1 12 Tf 72 700 Td (Mustermann) Tj ET EMC"),
        });
        Assert.DoesNotContain("AUSGESCHRIEBENGEHEIM", Redacted(doc, OverTheName));
    }

    [Fact]
    public void Expansion_AsNamedProperties_Goes()
    {
        using var doc = Open(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = Font,
            [4] = "<</Type/Page/Parent 2 0 R/Contents 7 0 R/Resources<</Font<</F1 3 0 R>>/Properties<</MC0<</E(BENANNTGEHEIM)>>>>>>>>",
            [7] = ("", "/Span /MC0 BDC BT /F1 12 Tf 72 700 Td (Mustermann) Tj ET EMC"),
        });
        Assert.DoesNotContain("BENANNTGEHEIM", Redacted(doc, OverTheName));
    }

    /// <summary>
    /// The structure element of the redacted marked content loses its /E — like every replacement text of a structure element on a redacted page
    /// (the writer cannot tell which of them hold redacted text, so none stays).
    /// </summary>
    [Fact]
    public void Expansion_InTheStructureTree_Goes()
    {
        using var doc = Open(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 8 0 R/MarkInfo<</Marked true>>>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = Font,
            [4] = "<</Type/Page/Parent 2 0 R/Contents 7 0 R/StructParents 0/Resources<</Font<</F1 3 0 R>>>>>>",
            [7] = ("", "/Span <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Mustermann) Tj ET EMC " +
                       "/Span <</MCID 1>> BDC BT /F1 12 Tf 72 600 Td (Anderes) Tj ET EMC"),
            [8] = "<</Type/StructTreeRoot/K[9 0 R 10 0 R]>>",
            [9] = "<</S/Span/P 8 0 R/Pg 4 0 R/K 0/E(STRUKTURGEHEIM)>>",
            [10] = "<</S/Span/P 8 0 R/Pg 4 0 R/K 1/E(STRUKTURBLEIBT)>>",
        });
        var text = Redacted(doc, OverTheName);
        Assert.DoesNotContain("STRUKTURGEHEIM", text);
        Assert.DoesNotContain("STRUKTURBLEIBT", text);
        Assert.Contains("StructTreeRoot", text);   // the structure itself stays
    }

    /// <summary>
    /// A JBIG2 image with shared data (/JBIG2Globals) under a bar is written anew; the shared data then belongs to no image and leaves the file.
    /// The same file redacted beside the image keeps it.
    /// </summary>
    [Fact]
    public void Jbig2Globals_OfARedactedImage_LeaveTheFile()
    {
        const int size = 32;
        var bits = new byte[size / 8 * size];
        for (var y = 0; y < size; y++) bits[y * (size / 8) + (y % 4)] = 0x0F;   // some black among white (0 bits are black)
        var data = Encoding.Latin1.GetString(Jbig2Encoder.Generic(bits, size, size, size / 8));
        Dictionary<int, object> objects = new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [4] = "<</Type/Page/Parent 2 0 R/Contents 7 0 R/Resources<</XObject<</Im1 5 0 R>>>>>>",
            [5] = ($"/Type/XObject/Subtype/Image/Width {size}/Height {size}/ColorSpace/DeviceGray/BitsPerComponent 1/Filter/JBIG2Decode" +
                   "/DecodeParms<</JBIG2Globals 6 0 R>>", data),
            [6] = ("/Merkmal(GLOBALGEHEIM)", ""),
            [7] = ("", "q 200 0 0 200 100 500 cm /Im1 Do Q"),
        };
        using (var doc = Open(objects)) Assert.DoesNotContain("GLOBALGEHEIM", Redacted(doc, new PdfRectangle(90, 490, 200, 710)));
        using (var doc = Open(objects)) Assert.Contains("GLOBALGEHEIM", Redacted(doc, new PdfRectangle(400, 100, 500, 200)));
    }

    /// <summary>A form with XFA data (the value of a field also in /datasets) and /TM: a bar over the field — nothing of it stays in the file (B-7).</summary>
    [Fact]
    public void FormFieldUnderABar_XfaDataAndMappingNameGo()
    {
        using var doc = Open(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/AcroForm<</Fields[8 0 R]/XFA 9 0 R/DA(/Helv 0 Tf 0 g)>>>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = Font,
            [4] = "<</Type/Page/Parent 2 0 R/Contents 7 0 R/Annots[8 0 R]/Resources<</Font<</F1 3 0 R>>>>>>",
            [7] = ("", "BT /F1 12 Tf 72 600 Td (Sichtbar) Tj ET"),
            [8] = "<</Type/Annot/Subtype/Widget/FT/Tx/T(name)/TM(FELDGEHEIM)/V(WERTGEHEIM)/Rect[70 690 300 715]/P 4 0 R>>",
            [9] = ("", "<xdp:xdp><xfa:datasets><xfa:data><form><name>WERTGEHEIM</name></form></xfa:data></xfa:datasets></xdp:xdp>"),
        });
        var text = Redacted(doc, OverTheName);
        Assert.DoesNotContain("WERTGEHEIM", text);
        Assert.DoesNotContain("FELDGEHEIM", text);
        Assert.Contains("Sichtbar", text);
    }

    /// <summary>
    /// A form with XFA data, the redacted name only in the page text and in the XFA data of another field: the XFA data go with the redaction
    /// (B-7), the fields stay.
    /// </summary>
    [Fact]
    public void XfaData_GoWithARedaction_FieldsStay()
    {
        using var doc = Open(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/AcroForm<</Fields[8 0 R]/XFA 9 0 R/DA(/Helv 0 Tf 0 g)>>/NeedsRendering false>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = Font,
            [4] = "<</Type/Page/Parent 2 0 R/Contents 7 0 R/Annots[8 0 R]/Resources<</Font<</F1 3 0 R>>>>>>",
            [7] = ("", "BT /F1 12 Tf 72 700 Td (Mustermann) Tj ET"),
            [8] = "<</Type/Annot/Subtype/Widget/FT/Tx/T(ort)/V(Musterstadt)/Rect[70 400 300 425]/P 4 0 R/AP<</N 10 0 R>>>>",
            [9] = ("", "<xdp:xdp><xfa:datasets><xfa:data><form><name>Mustermann</name><ort>Musterstadt</ort></form></xfa:data></xfa:datasets></xdp:xdp>"),
            [10] = ("/Type/XObject/Subtype/Form/BBox[0 0 230 25]/Resources<</Font<</F1 3 0 R>>>>", "/Tx BMC BT /F1 12 Tf 2 8 Td (Musterstadt) Tj ET EMC"),
        });
        var text = Redacted(doc, OverTheName);
        Assert.DoesNotContain("Mustermann", text);
        Assert.DoesNotContain("xfa:datasets", text);
        Assert.Contains("Musterstadt", text);   // the field itself stays
    }

}
