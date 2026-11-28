using System.Text.Json;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Cli;

/// <summary>
/// <c>pdf-knead textzeilen &lt;file&gt; [--seiten a-b]</c>: every text line of the pages as the own reader sees it, one JSON object per line —
/// page, page size, block, the bounds of the block, the start of the line on its baseline (page space, points), the angle, and the runs with
/// font name, size, bold, italic, colour, underline, strike-through and text. For the second, independent count of the standards
/// (docs/normabgleich/neupruefung): headings, notes, tables, the page frame and the change marks are told apart by these fields. The output
/// goes to a pipe, never into a file of the repository (it holds the text of the document).
/// </summary>
internal static class TextLinesCheck
{
    private static readonly PdfName Annots = PdfName.Get("Annots"), Subtype = PdfName.Get("Subtype"), C = PdfName.Get("C"),
        Rect = PdfName.Get("Rect"), QuadPoints = PdfName.Get("QuadPoints"), Contents = PdfName.Get("Contents"), Irt = PdfName.Get("IRT"),
        State = PdfName.Get("State"), StateModel = PdfName.Get("StateModel"), Nm = PdfName.Get("NM"), T = PdfName.Get("T");

    /// <summary>The kinds of annotation that carry the changes of a reviewed file: notes and their replies, and the markup of text.</summary>
    private static readonly HashSet<string> MarkupKinds = ["Text", "StrikeOut", "Caret", "Highlight", "Underline", "Squiggly", "FreeText"];

    /// <summary>
    /// <c>--anmerkungen</c>: the notes and the text markup of the pages, one JSON object per annotation — page, object number, kind, colour
    /// (0xRRGGBB from /C), rectangle, the text under its /QuadPoints as the own reader places it (struck through: the deleted words),
    /// /Contents (a caret: the inserted text), the object number it replies to (/IRT) and the review state of a reply (/State with
    /// /StateModel). A norm file that carries its corrections as annotations (ISO 32000-2 EC3) changes its text only through them.
    /// </summary>
    public static int Annotations(PdfDocument doc, int first, int last)
    {
        using var output = Console.OpenStandardOutput();
        return Annotations(doc, first, last, output);
    }

    /// <summary>The annotations of pages <paramref name="first"/> … <paramref name="last"/> (1-based) into <paramref name="output"/>.</summary>
    public static int Annotations(PdfDocument doc, int first, int last, Stream output)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var newline = "\n"u8.ToArray();
        for (var i = Math.Max(0, first - 1); i < Math.Min(doc.PageCount, last); i++)
        {
            var page = doc.Pages[i];
            if (page.Dictionary.Get(Annots) is not PdfArray annots) continue;
            TextGlyph[]? glyphs = null;
            for (var a = 0; a < annots.Count; a++)
            {
                if (annots.Get(a) is not PdfDictionary annot || annot.GetName(Subtype)?.Value is not { } kind || !MarkupKinds.Contains(kind)) continue;
                writer.WriteStartObject();
                writer.WriteNumber("s", i + 1);
                if (annots[a] is PdfReference own) writer.WriteNumber("id", own.Id.Number);
                writer.WriteString("art", kind);
                if (annot.GetArray(C) is { Count: 3 } colour)
                    writer.WriteString("farbe", string.Concat(Enumerable.Range(0, 3).Select(k =>
                        ((int)Math.Round(Math.Clamp(colour.GetNumber(k) ?? 0, 0, 1) * 255)).ToString("X2"))));
                if (PdfRectangle.From(annot.Get(Rect)) is { } rect)
                {
                    writer.WriteStartArray("rect");
                    foreach (var v in new[] { rect.Left, rect.Bottom, rect.Right, rect.Top }) writer.WriteNumberValue(Math.Round(v, 2));
                    writer.WriteEndArray();
                }
                if (annot.GetArray(QuadPoints) is { Count: >= 8 } quads)
                {
                    glyphs ??= PageInterpreter.BuildText(doc, page);
                    writer.WriteString("unter", TextUnder(glyphs, quads));
                }
                if (annot.GetString(Contents) is { } contents) writer.WriteString("inhalt", contents.ToText());
                if (annot.GetString(T) is { } author) writer.WriteString("von", author.ToText());
                if (annot.GetString(Nm) is { } name) writer.WriteString("nm", name.ToText());
                if (annot.GetRaw(Irt) is PdfReference reply) writer.WriteNumber("irt", reply.Id.Number);
                if (annot.GetString(State) is { } state) writer.WriteString("status", state.ToText());
                if (annot.GetString(StateModel) is { } model) writer.WriteString("statusmodell", model.ToText());
                writer.WriteEndObject();
                writer.Flush();
                output.Write(newline);
                writer.Reset();
            }
        }
        return 0;
    }

    /// <summary>The characters whose centre lies in one of the quadrilaterals (bounds widened by a tenth of their height), in the order of
    /// the content stream, the quadrilaterals one after the other.</summary>
    private static string TextUnder(TextGlyph[] glyphs, PdfArray quads)
    {
        var text = new System.Text.StringBuilder();
        for (var q = 0; q + 8 <= quads.Count && q < 8 * 256; q += 8)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (var k = 0; k < 8; k += 2)
            {
                var x = quads.GetNumber(q + k) ?? 0;
                var y = quads.GetNumber(q + k + 1) ?? 0;
                (minX, maxX, minY, maxY) = (Math.Min(minX, x), Math.Max(maxX, x), Math.Min(minY, y), Math.Max(maxY, y));
            }
            var pad = (maxY - minY) / 10;
            var part = new System.Text.StringBuilder();
            foreach (var g in glyphs)
            {
                var cx = (g.X0 + g.X1 + g.X2 + g.X3) / 4;
                var cy = (g.Y0 + g.Y1 + g.Y2 + g.Y3) / 4;
                if (cx >= minX - pad && cx <= maxX + pad && cy >= minY - pad && cy <= maxY + pad) part.Append(g.Text);
            }
            // the space after the last word reaches into the widened bounds; it is no part of the marked words
            if (part.ToString().Trim() is { Length: > 0 } words) text.Append(text.Length > 0 ? " " : "").Append(words);
        }
        return text.ToString();
    }

    /// <summary><c>--lesezeichen</c>: the outline of the document, one JSON object per item — depth, title and page (the inventory of the
    /// re-check compares it with the headings of both counts).</summary>
    public static int Outline(PdfDocument doc)
    {
        using var output = Console.OpenStandardOutput();
        return Outline(doc, output);
    }

    /// <summary>The outline into <paramref name="output"/> (the tests read it back).</summary>
    public static int Outline(PdfDocument doc, Stream output)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var newline = "\n"u8.ToArray();
        void Walk(IReadOnlyList<OutlineItem> items, int depth)
        {
            if (depth > 64) return;
            foreach (var item in items)
            {
                writer.WriteStartObject();
                writer.WriteNumber("o", depth);
                writer.WriteString("t", item.Title);
                if (item.PageIndex is { } index) writer.WriteNumber("s", index + 1);
                writer.WriteEndObject();
                writer.Flush();
                output.Write(newline);
                writer.Reset();
                Walk(item.Children, depth + 1);
            }
        }
        Walk(PdfOutline.Read(doc), 0);
        return 0;
    }

    public static int Run(PdfDocument doc, int first, int last)
    {
        using var output = Console.OpenStandardOutput();
        return Run(doc, first, last, output);
    }

    /// <summary>The lines of pages <paramref name="first"/> … <paramref name="last"/> (1-based) into <paramref name="output"/>.</summary>
    public static int Run(PdfDocument doc, int first, int last, Stream output)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var newline = "\n"u8.ToArray();
        for (var i = Math.Max(0, first - 1); i < Math.Min(doc.PageCount, last); i++)
        {
            var page = doc.Pages[i];
            var box = page.CropBox;
            var blocks = TextBlocks.Find(doc, page);
            for (var b = 0; b < blocks.Count; b++)
            {
                var block = blocks[b];
                var bounds = block.Bounds;
                foreach (var line in block.Lines)
                {
                    var (x, y) = block.FrameToPage.Transform(line.Start, line.Baseline);
                    writer.WriteStartObject();
                    writer.WriteNumber("s", i + 1);
                    writer.WriteNumber("w", Math.Round(box.Width, 2));
                    writer.WriteNumber("h", Math.Round(box.Height, 2));
                    writer.WriteNumber("b", b);
                    writer.WriteStartArray("box");
                    foreach (var v in new[] { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom }) writer.WriteNumberValue(Math.Round(v, 2));
                    writer.WriteEndArray();
                    writer.WriteNumber("a", Math.Round(block.Angle, 1));
                    writer.WriteNumber("x", Math.Round(x, 2));
                    writer.WriteNumber("y", Math.Round(y, 2));
                    writer.WriteNumber("len", Math.Round(line.End - line.Start, 2));
                    writer.WriteBoolean("hart", line.HardBreak);
                    writer.WriteStartArray("runs");
                    foreach (var run in line.Runs)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("f", run.Format.FontName);
                        writer.WriteNumber("z", Math.Round(run.Format.Size, 2));
                        if (run.Format.Bold) writer.WriteBoolean("fett", true);
                        if (run.Format.Italic) writer.WriteBoolean("kursiv", true);
                        if (run.Format.Rise != 0) writer.WriteNumber("hoch", Math.Round(run.Format.Rise, 2));
                        // colour and the lines found with the text: the change marks of a norm file (struck through and coloured: deleted;
                        // underlined and coloured: inserted) must not be counted as the text that applies
                        if (run.Format.Color != 0) writer.WriteString("farbe", run.Format.Color.ToString("X6"));
                        if (run.Format.Underline) writer.WriteBoolean("unter", true);
                        if (run.Format.Strikeout) writer.WriteBoolean("durch", true);
                        writer.WriteString("t", run.Text);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.Flush();
                    output.Write(newline);
                    writer.Reset();
                }
            }
        }
        return 0;
    }
}
