using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// What stays of a field's appearance when its value changes. ISO 32000-2, 12.7.4.3, sentences 46–47: an existing appearance stream of a field
/// with variable text is updated, not replaced — only its marked-content section <c>/Tx BMC … EMC</c> gets the new text; without such a section
/// the new text is appended. Background and border the appearance draws outside the section stay (bug1883609: the yellow background of a choice
/// field went when PDF-Knead settled the form's appearances for PDF 2.0, docs/FEHLERGEDAECHTNIS.md, No. 815).
/// </summary>
internal static class FieldFrame
{
    private static readonly PdfName Ap = PdfName.Get("AP"), Ff = PdfName.Get("Ff"), Ft = PdfName.Get("FT"), Parent = PdfName.Get("Parent");

    /// <summary>Operators read at most: the appearance of a field is small; one with more is drawn anew instead (as when it is damaged).</summary>
    private const int MaxOperators = 100_000;

    /// <summary>The appearance can be updated: <paramref name="Frame"/> is what stays of it (null if nothing stays, the section was all).</summary>
    internal readonly record struct Update(PdfStream? Frame);

    /// <summary>
    /// The update of the field's normal appearance: its stream without the <c>/Tx</c> section — the stream itself where it has none and shows no
    /// text. Null where the appearance is not updated but drawn anew from the field's properties (/MK, /DA), as before: no appearance stream of
    /// its own (none, or one per state), a rich text field (12.7.4.3, sentence 19: regenerated entirely), an appearance that cannot be read or
    /// whose section does not end, and one that shows text without a <c>/Tx</c> section — appended, the new value would stand over the old one
    /// (decided on 13.11.2026: built anew, a deviation from sentence 47 — docs/ENTSCHEIDUNGEN.md).
    /// </summary>
    /// <param name="doc">The document of the widget.</param>
    /// <param name="widget">The widget annotation dictionary.</param>
    /// <returns>The update, or null.</returns>
    public static Update? Of(PdfDocument doc, PdfDictionary widget)
    {
        if (widget.GetDictionary(Ap)?.Get(PdfName.N) is not PdfStream stream) return null;
        if (Inherited(widget, Ft) is PdfName { Value: "Tx" } && Inherited(widget, Ff) is PdfInteger flags && (flags.Value & (1 << 25)) != 0) return null;
        byte[] data;
        try
        {
            data = doc.GetStreamData(stream);
        }
        catch (Exception e) when (e is PdfException or IOException or InvalidDataException)
        {
            return null;
        }
        if (Section(data) is not var (start, end, showsText)) return null;
        if (start < 0)
            return showsText ? null : new Update(Copy(stream, data));
        var kept = new byte[start + (data.Length - end)];
        data.AsSpan(0, start).CopyTo(kept);
        data.AsSpan(end).CopyTo(kept.AsSpan(start));
        return new Update(kept.AsSpan().ContainsAnyExcept(" \t\r\n\f\0"u8) ? Copy(stream, kept) : null);
    }

    /// <summary>
    /// The first <c>/Tx</c> marked-content section (from its operand to the end of its <c>EMC</c>; -1, -1 without one) and whether the content
    /// outside it shows text (an XObject counts, as it may); null if the section does not end or the content is too long to read.
    /// </summary>
    internal static (int Start, int End, bool ShowsText)? Section(byte[] data)
    {
        var parser = new ContentParser(data);
        var depth = 0;
        var txDepth = -1;
        int start = -1, end = -1, before = 0;
        var showsText = false;
        for (var n = 0; n < MaxOperators; n++)
        {
            var op = parser.Next();
            if (op is null)
                return txDepth >= 0 ? null : (start, end, showsText);
            switch (op)
            {
                case "BMC" or "BDC":
                    if (txDepth < 0 && start < 0 && parser.Operands.Count > 0 && parser.Operands[0].Name?.Value == "Tx")
                    {
                        start = before;
                        txDepth = depth;
                    }
                    depth++;
                    break;
                case "EMC":
                    depth = Math.Max(0, depth - 1);
                    if (txDepth >= 0 && depth == txDepth)
                    {
                        end = parser.Position;
                        txDepth = -1;
                    }
                    break;
                case "Tj" or "TJ" or "'" or "\"" or "Do" when txDepth < 0:
                    showsText = true;
                    break;
            }
            before = parser.Position;
        }
        return null;
    }

    /// <summary>A form with the dictionary of <paramref name="stream"/> (read in its document) and the decoded content <paramref name="content"/>.</summary>
    private static PdfStream Copy(PdfStream stream, byte[] content)
    {
        var dict = new PdfDictionary(stream.Dictionary.Count) { Resolver = stream.Dictionary.Resolver };
        foreach (var (key, _) in stream.Dictionary.Entries)
            if (key.Value is not ("Length" or "Filter" or "DecodeParms" or "DL" or "F" or "FFilter" or "FDecodeParms"))
                dict.Set(key, stream.Dictionary.GetRaw(key)!);
        return new PdfStream(dict, content);
    }

    /// <summary>Field attribute, also inherited from the parent field (12.7.4.1); depth limited against cycles.</summary>
    private static PdfObject? Inherited(PdfDictionary field, PdfName key)
    {
        var current = field;
        for (var depth = 0; depth < 32 && current is not null; depth++)
        {
            if (current.Get(key) is { } value and not PdfNull) return value;
            current = current.GetDictionary(Parent);
        }
        return null;
    }
}
