using System.Text.Json;
using PdfKnead.Cli;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Tests.Pdf;

namespace PdfKnead.Tests.Cli;

/// <summary>
/// <c>pdf-knead textzeilen</c>, the reader of the second count of the standards (docs/normabgleich): one JSON object per line with page, page
/// size, block, start of the baseline in page space, and the runs with font, size, bold and text; <c>--lesezeichen</c> gives the outline.
/// The count tells headings, notes and the page frame apart by these fields, so they must be right.
/// </summary>
public sealed class TextLinesCheckTests
{
    /// <summary>Two pages of 300 × 400: a bold heading and a line of body text on page 1, one line on page 2; an outline of two items, the
    /// second with a child.</summary>
    private static byte[] Sample() => DocumentStructureNormTests.FileOf(
        "<< /Type /Catalog /Pages 2 0 R /Outlines 8 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << /Font << /F1 5 0 R /F2 7 0 R >> >> /Contents 4 0 R >>",
        ContentStream("BT /F1 14 Tf 30 350 Td (1 Scope) Tj ET BT /F2 10 Tf 30 300 Td (Body text here) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << /Font << /F2 7 0 R >> >> /Contents 13 0 R >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>",
        "<< /Type /Outlines /First 9 0 R /Last 10 0 R /Count 3 >>",
        "<< /Title (1 Scope) /Parent 8 0 R /Next 10 0 R /Dest [3 0 R /XYZ 0 400 0] >>",
        "<< /Title (2 Terms) /Parent 8 0 R /Prev 9 0 R /First 11 0 R /Last 11 0 R /Count 1 /Dest [6 0 R /XYZ 0 400 0] >>",
        "<< /Title (2.1 General) /Parent 10 0 R /Dest [6 0 R /XYZ 0 200 0] >>",
        "<< >>",
        ContentStream("BT /F2 10 Tf 40 200 Td (Second page) Tj ET"));

    private static string ContentStream(string data) => $"<< /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static List<JsonElement> Lines(Func<PdfDocument, Stream, int> run, byte[]? file = null)
    {
        using var doc = PdfDocument.Open(file ?? Sample());
        using var output = new MemoryStream();
        Assert.Equal(0, run(doc, output));
        return System.Text.Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    private static string Text(JsonElement line) => string.Concat(line.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("t").GetString()));

    [Fact]
    public void Lines_PageSizePositionFontSizeAndBold()
    {
        var lines = Lines((doc, output) => TextLinesCheck.Run(doc, 1, 2, output));
        Assert.Equal(["1 Scope", "Body text here", "Second page"], lines.Select(Text));
        var heading = lines[0];
        Assert.Equal(1, heading.GetProperty("s").GetInt32());
        Assert.Equal(300, heading.GetProperty("w").GetDouble());
        Assert.Equal(400, heading.GetProperty("h").GetDouble());
        Assert.Equal(30, heading.GetProperty("x").GetDouble(), 1);
        Assert.Equal(350, heading.GetProperty("y").GetDouble(), 1);   // the baseline in page space, origin at the bottom
        var run = heading.GetProperty("runs")[0];
        Assert.Equal(14, run.GetProperty("z").GetDouble());
        Assert.True(run.GetProperty("fett").GetBoolean());
        var body = lines[1].GetProperty("runs")[0];
        Assert.Equal(10, body.GetProperty("z").GetDouble());
        Assert.False(body.TryGetProperty("fett", out _));   // only written where true
        Assert.Equal(300, lines[1].GetProperty("y").GetDouble(), 1);
        Assert.Equal(2, lines[2].GetProperty("s").GetInt32());
    }

    /// <summary>The change marks of a norm file: a red line struck through (deleted text), a blue line underlined (inserted text), a black
    /// line without marks.</summary>
    [Fact]
    public void Lines_ColourUnderlineAndStrikeThrough()
    {
        var file = DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << /Font << /F2 5 0 R >> >> /Contents 4 0 R >>",
            ContentStream("1 0 0 rg BT /F2 10 Tf 30 300 Td (Deleted words) Tj ET 30 302.8 62 0.5 re f " +
                          "0 0 1 rg BT /F2 10 Tf 30 270 Td (Inserted words) Tj ET 30 268.5 64 0.5 re f " +
                          "0 g BT /F2 10 Tf 30 240 Td (Plain words) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>");
        var lines = Lines((doc, output) => TextLinesCheck.Run(doc, 1, 1, output), file);
        Assert.Equal(["Deleted words", "Inserted words", "Plain words"], lines.Select(Text));
        var deleted = lines[0].GetProperty("runs")[0];
        Assert.Equal("FF0000", deleted.GetProperty("farbe").GetString());
        Assert.True(deleted.GetProperty("durch").GetBoolean());
        Assert.False(deleted.TryGetProperty("unter", out _));
        var inserted = lines[1].GetProperty("runs")[0];
        Assert.Equal("0000FF", inserted.GetProperty("farbe").GetString());
        Assert.True(inserted.GetProperty("unter").GetBoolean());
        Assert.False(inserted.TryGetProperty("durch", out _));
        var plain = lines[2].GetProperty("runs")[0];
        Assert.False(plain.TryGetProperty("farbe", out _) || plain.TryGetProperty("unter", out _) || plain.TryGetProperty("durch", out _));
    }

    /// <summary>A reviewed file as ISO 32000-2 EC3 carries its corrections: a red strike-out over „Old“, a blue caret with the inserted
    /// text, a note with a reply that sets its review state; a link is no markup and stays out.</summary>
    [Fact]
    public void Annotations_StrikeOutWithItsWords_CaretWithItsText_ReplyWithItsState()
    {
        var file = DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << /Font << /F2 5 0 R >> >> /Contents 4 0 R " +
            "/Annots [6 0 R 7 0 R 8 0 R 9 0 R 10 0 R] >>",
            ContentStream("BT /F2 10 Tf 30 300 Td (Old words here) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>",
            "<< /Type /Annot /Subtype /StrikeOut /Rect [29 298 47 310] /QuadPoints [29 310 47 310 29 298 47 298] /C [1 0 0] " +
            "/Contents (Issue #7) /T (Reviewer) >>",
            "<< /Type /Annot /Subtype /Caret /Rect [95 298 101 304] /C [0 0 1] /Contents (inserted text) >>",
            "<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /Contents (A note) /NM (n-1) >>",
            "<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /IRT 8 0 R /State (Completed) /StateModel (Review) >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] >>");
        var items = Lines((doc, output) => TextLinesCheck.Annotations(doc, 1, 1, output), file);
        Assert.Equal(["StrikeOut", "Caret", "Text", "Text"], items.Select(o => o.GetProperty("art").GetString()));
        var strike = items[0];
        Assert.Equal(6, strike.GetProperty("id").GetInt32());
        Assert.Equal("FF0000", strike.GetProperty("farbe").GetString());
        Assert.Equal("Old", strike.GetProperty("unter").GetString());   // only the words under the quadrilateral
        Assert.Equal("Issue #7", strike.GetProperty("inhalt").GetString());
        Assert.Equal("Reviewer", strike.GetProperty("von").GetString());
        Assert.Equal("0000FF", items[1].GetProperty("farbe").GetString());
        Assert.Equal("inserted text", items[1].GetProperty("inhalt").GetString());
        Assert.False(items[1].TryGetProperty("unter", out _));
        Assert.Equal("n-1", items[2].GetProperty("nm").GetString());
        Assert.Equal(8, items[3].GetProperty("irt").GetInt32());
        Assert.Equal("Completed", items[3].GetProperty("status").GetString());
        Assert.Equal("Review", items[3].GetProperty("statusmodell").GetString());
    }

    [Fact]
    public void Lines_OnlyThePagesAsked()
    {
        var lines = Lines((doc, output) => TextLinesCheck.Run(doc, 2, 2, output));
        Assert.Equal(["Second page"], lines.Select(Text));
        Assert.Empty(Lines((doc, output) => TextLinesCheck.Run(doc, 3, 9, output)));   // past the end: nothing, no error
    }

    [Fact]
    public void Outline_DepthTitleAndPage()
    {
        var items = Lines(TextLinesCheck.Outline);
        Assert.Equal([(0, "1 Scope", 1), (0, "2 Terms", 2), (1, "2.1 General", 2)],
            items.Select(o => (o.GetProperty("o").GetInt32(), o.GetProperty("t").GetString()!, o.GetProperty("s").GetInt32())));
    }
}
