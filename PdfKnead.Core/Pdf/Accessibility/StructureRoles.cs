using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>
/// What the writer and the redactor need to know of the structure of a tagged file: whether it is tagged at all, the standard type an element
/// stands for (through /RoleMap and /RoleMapNS; the neutral description of redacted content is in <see cref="FixedTexts"/>) and whether
/// a content stream marks everything it paints as an artifact.
/// </summary>
internal static class StructureRoles
{
    /// <summary>A tagged file: the catalogue marks it (/MarkInfo /Marked true) and it has a structure tree.</summary>
    public static bool IsTagged(PdfDictionary? catalog) =>
        catalog is not null && catalog.GetDictionary(PdfName.Get("MarkInfo"))?.GetBoolean(PdfName.Get("Marked")) == true &&
        catalog.Get(PdfName.Get("StructTreeRoot")) is PdfDictionary;

    /// <summary>The name of the standard type a structure element stands for (<see cref="StandardStructure.Resolve"/>).</summary>
    /// <param name="element">The structure element.</param>
    /// <param name="root">The structure tree root (its /RoleMap); null: no role map.</param>
    /// <returns>The name; null if the element has no type or stands for no standard type.</returns>
    public static string? Resolve(PdfDictionary element, PdfDictionary? root) => StandardStructure.Resolve(element, root)?.Name;

    /// <summary>
    /// Operators that paint (ISO 32000-2, 8.5.3, 9.4.3, 8.8, 8.10, 8.7.4.2, 8.9.7): paths, text, XObjects, shadings and inline images — which
    /// <see cref="ContentParser"/> returns as „BI“ (with the whole image), never as „EI“ (before: only „EI“ was listed, an inline image painted
    /// nothing; docs/FEHLERGEDAECHTNIS.md, No. 629).
    /// </summary>
    private static readonly HashSet<string> Painting = new(["S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "Tj", "TJ", "'", "\"", "Do", "sh", "BI"], StringComparer.Ordinal);

    /// <summary>Whether an operator paints (see <see cref="Painting"/>).</summary>
    public static bool Paints(string op) => Painting.Contains(op);

    /// <summary>Whether everything a content stream paints lies in marked content of the tag /Artifact (nothing it paints is real content).</summary>
    /// <param name="data">The decoded content.</param>
    public static bool OnlyArtifacts(byte[] data)
    {
        var parser = new ContentParser(data);
        var marks = new Stack<bool>();   // per open marked-content sequence: inside an artifact
        string? op;
        while ((op = parser.Next()) is not null)
            switch (op)
            {
                case "BMC" or "BDC":
                    var artifact = parser.Operands.Count >= 1 && parser.Operands[0].Object is PdfName { Value: "Artifact" };
                    marks.Push(artifact || marks.Count > 0 && marks.Peek());
                    break;
                case "EMC":
                    marks.TryPop(out _);
                    break;
                default:
                    if (Painting.Contains(op) && !(marks.Count > 0 && marks.Peek())) return false;
                    break;
            }
        return true;
    }

    /// <summary>How many marked-content sequences (BMC, BDC) a content stream leaves open at its end (an EMC without its sequence closes nothing).</summary>
    /// <param name="data">The decoded content.</param>
    public static int OpenMarks(byte[] data)
    {
        var parser = new ContentParser(data);
        var open = 0;
        string? op;
        while ((op = parser.Next()) is not null)
            if (op is "BMC" or "BDC") open++;
            else if (op == "EMC" && open > 0) open--;
        return Math.Min(open, 4096);
    }
}
