using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// The nesting of graphics states in a content stream within the 28 levels PDF/A allows (ISO 19005-2, 6.1.13; ISO 19005-1, 6.1.12; counted per
/// content stream, as strict readers count it). First what changes nothing: a q that is never restored and a Q without its q go, a pair whose
/// content changes no part of the graphics state (paths, text without text state, images, forms, shadings, marked content) loses its q and Q,
/// two pairs directly inside each other become one. What is still deeper moves into a form (<c>Do</c> saves and restores the graphics state as
/// <c>q … Q</c> does, and a form's content counts from zero), where the content of the pair stands whole: text objects, marked content and
/// compatibility sections begin and end inside it, and it marks no content for the structure (MCID).
/// </summary>
internal static class ContentNesting
{
    /// <summary>The deepest nesting PDF/A allows.</summary>
    public const int MaxDepth = 28;

    /// <summary>The deepest nesting of a content stream (a Q without its q restores nothing).</summary>
    /// <param name="data">The content.</param>
    /// <returns>The number of q open at the deepest point.</returns>
    public static int Depth(byte[] data)
    {
        var parser = new ContentParser(data);
        var (depth, deepest) = (0, 0);
        string? op;
        while ((op = parser.Next()) is not null)
            if (op == "q") deepest = Math.Max(deepest, ++depth);
            else if (op == "Q") depth = Math.Max(0, depth - 1);
        return deepest;
    }

    /// <summary>Operators that change no part of the graphics state (ISO 32000-1, 8.4, 9.3): a pair around only these restores nothing.</summary>
    private static readonly HashSet<string> Unchanging = new(["m", "l", "c", "v", "y", "h", "re", "S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "n",
        "BT", "ET", "Td", "TD", "Tm", "T*", "Tj", "TJ", "'", "Do", "sh", "BI", "BMC", "BDC", "EMC", "MP", "DP", "BX", "EX"], StringComparer.Ordinal);

    private readonly record struct Op(string Name, int Start, int End, bool Marks);

    /// <summary>
    /// The content with its nesting reduced (see the class). Content that is deeper than <see cref="MaxDepth"/> still — with no
    /// <paramref name="nest"/>, or where no pair can move — stays so.
    /// </summary>
    /// <param name="data">The content (as <see cref="ContentConformance.Rewrite"/> writes it).</param>
    /// <param name="nest">Makes a form of the given content and returns the name to draw it with; null: nothing moves into a form.</param>
    /// <returns>The new content.</returns>
    public static byte[] Reduce(byte[] data, Func<byte[], PdfName>? nest)
    {
        var ops = new List<Op>();
        var parser = new ContentParser(data);
        var before = 0;
        string? name;
        while ((name = parser.Next()) is not null)
        {
            var marks = name == "BDC" && parser.Operands.Count == 2 && parser.Operands[1].Object is PdfDictionary properties &&
                        properties.ContainsKey(PdfName.Get("MCID"));
            ops.Add(new Op(name, before, parser.Position, marks));
            before = parser.Position;
        }
        var kept = new bool[ops.Count];
        Array.Fill(kept, true);
        // the pairs: q and its Q; a q never restored and a Q without its q go
        var partner = new int[ops.Count];
        Array.Fill(partner, -1);
        var open = new Stack<int>();
        for (var i = 0; i < ops.Count; i++)
            if (ops[i].Name == "q") open.Push(i);
            else if (ops[i].Name == "Q")
            {
                if (open.TryPop(out var q)) (partner[q], partner[i]) = (i, q);
                else kept[i] = false;
            }
        foreach (var q in open) kept[q] = false;
        // from the inside out: a pair that restores nothing, and the inner of two pairs directly inside each other
        var pairs = Enumerable.Range(0, ops.Count).Where(i => kept[i] && ops[i].Name == "q").OrderBy(i => partner[i] - i).ToList();
        foreach (var q in pairs)
        {
            var end = partner[q];
            var unchanging = true;
            int? only = null;
            var single = true;
            for (var i = q + 1; i < end; i++)
            {
                if (!kept[i]) continue;
                if (ops[i].Name == "q")
                {
                    if (only is null && single) only = i;
                    else single = false;
                    i = partner[i];
                    continue;
                }
                single = false;
                if (!Unchanging.Contains(ops[i].Name)) unchanging = false;
            }
            if (unchanging) (kept[q], kept[end]) = (false, false);
            else if (single && only is { } inner) (kept[inner], kept[partner[inner]]) = (false, false);
        }
        var output = new MemoryStream(data.Length);
        Write(output, data, ops, kept, partner, 0, ops.Count, 0, nest);
        return output.ToArray();
    }

    /// <summary>Writes the operators from <paramref name="from"/> to <paramref name="to"/> at nesting <paramref name="depth"/>.</summary>
    private static void Write(MemoryStream output, byte[] data, List<Op> ops, bool[] kept, int[] partner, int from, int to, int depth, Func<byte[], PdfName>? nest)
    {
        var text = false;
        for (var i = from; i < to; i++)
        {
            if (!kept[i]) continue;
            var op = ops[i];
            if (op.Name == "BT") text = true;
            else if (op.Name == "ET") text = false;
            if (op.Name == "q" && depth == MaxDepth && nest is not null && !text && Movable(ops, kept, i + 1, partner[i]))
            {
                // the pair as a form: its content counts from zero there (and is reduced the same way)
                var inner = new MemoryStream();
                Write(inner, data, ops, kept, partner, i + 1, partner[i], 0, nest);
                var formName = nest(inner.ToArray());
                Separate(output);
                PdfSerializer.Write(formName, output);
                output.Write(" Do\n"u8);
                i = partner[i];
                continue;
            }
            if (op.Name == "q") depth++;
            else if (op.Name == "Q") depth = Math.Max(0, depth - 1);
            if (op.End > op.Start && !White(data[op.Start])) Separate(output);
            output.Write(data.AsSpan(op.Start, op.End - op.Start));
        }
        Separate(output);
    }

    /// <summary>A line end after what was written, so that two operators that stood apart with the operators between them gone stay apart.</summary>
    private static void Separate(MemoryStream output)
    {
        if (output.Length > 0 && !White(output.GetBuffer()[output.Length - 1])) output.WriteByte((byte)'\n');
    }

    private static bool White(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    /// <summary>Whether the operators from <paramref name="from"/> to <paramref name="to"/> stand whole in a form (see the class).</summary>
    private static bool Movable(List<Op> ops, bool[] kept, int from, int to)
    {
        var (text, marked, compatibility) = (0, 0, 0);
        for (var i = from; i < to; i++)
        {
            if (!kept[i]) continue;
            var op = ops[i];
            if (op.Marks) return false;
            switch (op.Name)
            {
                case "BT": text++; break;
                case "ET": if (--text < 0) return false; break;
                case "BMC" or "BDC": marked++; break;
                case "EMC": if (--marked < 0) return false; break;
                case "BX": compatibility++; break;
                case "EX": if (--compatibility < 0) return false; break;
            }
        }
        return text == 0 && marked == 0 && compatibility == 0;
    }

    /// <summary>The content of a form that draws the content given (<see cref="Reduce"/>): unbounded (as large as an integer may be), unscaled.</summary>
    /// <param name="content">Its content.</param>
    /// <param name="resources">Its resources.</param>
    /// <returns>The form.</returns>
    internal static PdfStream Form(byte[] content, PdfObject resources)
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Type, PdfName.Get("XObject"));
        dict.Set(PdfName.Subtype, PdfName.Get("Form"));
        const int far = 1_000_000_000;
        dict.Set(PdfName.Get("BBox"), new PdfArray([PdfInteger.Of(-far), PdfInteger.Of(-far), PdfInteger.Of(far), PdfInteger.Of(far)]));
        dict.Set(PdfName.Resources, resources);
        return new PdfStream(dict, content);
    }

    /// <summary>The name of the <paramref name="n"/>-th form of a resource dictionary that has the names <paramref name="taken"/>.</summary>
    internal static PdfName FreeName(int n, Func<PdfName, bool> taken)
    {
        for (var k = 0; ; k++)
        {
            var candidate = PdfName.Get(k == 0 ? "PDFKneadNest" + n : "PDFKneadNest" + n + "_" + k);
            if (!taken(candidate)) return candidate;
        }
    }
}
