using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>The marked-content identifiers (MCID) a content stream uses (ISO 32000-2, 14.6.2 and 14.7.5.2).</summary>
internal static class MarkedContentIds
{
    private static readonly PdfName Mcid = PdfName.Get("MCID");

    /// <summary>
    /// The largest MCID in the content: of each BDC, from its inline property list or from the list it names in /Properties of the resources.
    /// </summary>
    /// <param name="content">The decoded content (all streams of a page in order).</param>
    /// <param name="properties">/Properties of the resources the content uses; null: none.</param>
    /// <returns>The largest MCID, -1 if the content marks none.</returns>
    public static int Largest(byte[] content, PdfDictionary? properties)
    {
        var parser = new ContentParser(content);
        var largest = -1;
        string? op;
        while ((op = parser.Next()) is not null)
        {
            if (op != "BDC" || parser.Operands.Count < 2) continue;
            var list = parser.Operands[1].Object switch
            {
                PdfDictionary inline => inline,
                PdfName name => properties?.Get(name) as PdfDictionary,
                _ => null,
            };
            if (list?.Get(Mcid) is PdfInteger { Value: >= 0 and <= int.MaxValue } id && id.Value > largest) largest = (int)id.Value;
        }
        return largest;
    }

    /// <summary>
    /// The MCIDs whose marked content paints something (<see cref="StructureRoles.Paints"/>: text, paths, images, forms, shadings) — directly
    /// or in marked content nested in it. A bracket that only sets state paints nothing.
    /// </summary>
    /// <param name="content">The decoded content (all streams of a page in order).</param>
    /// <param name="properties">/Properties of the resources the content uses; null: none.</param>
    /// <returns>The painting MCIDs.</returns>
    public static HashSet<int> Painting(byte[] content, PdfDictionary? properties)
    {
        var parser = new ContentParser(content);
        var painting = new HashSet<int>();
        var open = new List<int?>();   // the MCID of each bracket open (null: one without)
        string? op;
        while ((op = parser.Next()) is not null)
        {
            switch (op)
            {
                case "BDC":
                    var list = parser.Operands.Count < 2 ? null : parser.Operands[1].Object switch
                    {
                        PdfDictionary inline => inline,
                        PdfName name => properties?.Get(name) as PdfDictionary,
                        _ => null,
                    };
                    open.Add(list?.Get(Mcid) is PdfInteger { Value: >= 0 and <= int.MaxValue } id ? (int)id.Value : null);
                    break;
                case "BMC":
                    open.Add(null);
                    break;
                case "EMC":
                    if (open.Count > 0) open.RemoveAt(open.Count - 1);
                    break;
                default:
                    // (a text operator without characters paints nothing — a rewritten line leaves „[] TJ“)
                    if (StructureRoles.Paints(op) && !ShowsNothing(op, parser.Operands))
                        foreach (var mcid in open)
                            if (mcid is { } m) painting.Add(m);
                    break;
            }
            if (open.Count > 1000) open.RemoveAt(0);   // a crafted file without EMC
        }
        return painting;
    }

    /// <summary>
    /// The content with the marked content of the given MCIDs as artifact content: each such BDC (with its operands) replaced by
    /// <c>/Artifact BMC</c>, its EMC staying; everything else byte for byte as it was (nothing else is normalised). The content itself if none
    /// is found.
    /// </summary>
    /// <param name="content">The decoded content.</param>
    /// <param name="mcids">The MCIDs to become artifact content.</param>
    /// <param name="properties">/Properties of the resources the content uses (named property lists); null: none.</param>
    public static byte[] AsArtifacts(byte[] content, IReadOnlySet<int> mcids, PdfDictionary? properties)
    {
        var parser = new ContentParser(content);
        MemoryStream? output = null;
        var copied = 0;
        var before = 0;   // where the operands of the operator just read begin: after the operator before it
        string? op;
        while ((op = parser.Next()) is not null)
        {
            var end = parser.Position;
            if (op == "BDC" && parser.Operands.Count >= 2)
            {
                var list = parser.Operands[1].Object switch
                {
                    PdfDictionary inline => inline,
                    PdfName name => properties?.Get(name) as PdfDictionary,
                    _ => null,
                };
                if (list?.Get(Mcid) is PdfInteger { Value: >= 0 and <= int.MaxValue } id && mcids.Contains((int)id.Value))
                {
                    output ??= new MemoryStream(content.Length + 64);
                    output.Write(content, copied, before - copied);
                    output.Write("\n/Artifact BMC\n"u8);
                    copied = end;
                }
            }
            before = end;
        }
        if (output is null) return content;
        output.Write(content, copied, content.Length - copied);
        return output.ToArray();
    }

    /// <summary>
    /// The content with the property lists written into it (BDC and DP with an inline dictionary) changed where <paramref name="change"/> gives
    /// another (null: the list stays); everything else byte for byte as it was. The content itself if nothing changes.
    /// </summary>
    public static byte[] WithPropertyLists(byte[] content, Func<PdfDictionary, PdfDictionary?> change)
    {
        var parser = new ContentParser(content);
        MemoryStream? output = null;
        var copied = 0;
        var before = 0;
        string? op;
        while ((op = parser.Next()) is not null)
        {
            var end = parser.Position;
            if (op is "BDC" or "DP" && parser.Operands.Count >= 2 && parser.Operands[0].Object is PdfName tag &&
                parser.Operands[1].Object is PdfDictionary list && change(list) is { } changed)
            {
                output ??= new MemoryStream(content.Length + 64);
                output.Write(content, copied, before - copied);
                output.WriteByte((byte)'\n');
                Writing.PdfSerializer.Write(tag, output);
                output.WriteByte((byte)' ');
                Writing.PdfSerializer.Write(changed, output);
                output.Write(op == "BDC" ? " BDC\n"u8 : " DP\n"u8);
                copied = end;
            }
            before = end;
        }
        if (output is null) return content;
        output.Write(content, copied, content.Length - copied);
        return output.ToArray();
    }

    /// <summary>A text-showing operator whose strings are all empty (Tj, ', " with an empty string; TJ with no non-empty string).</summary>
    private static bool ShowsNothing(string op, IReadOnlyList<Operand> operands) => op switch
    {
        "Tj" or "'" or "\"" => operands.Count > 0 && operands[^1].Object is PdfString { Bytes.Length: 0 },
        "TJ" => operands.Count > 0 && operands[0].Object is PdfArray parts && !parts.Any(p => p is PdfString { Bytes.Length: > 0 }),
        _ => false,
    };
}
