namespace PdfKnead.Core.Pdf.Content;

/// <summary>
/// How content leaves the graphics state: how many q it would need before it so that no Q closes more than was opened, and how many Q
/// after it then close everything again. Readers ignore a Q without a q; content drawn after such content (bars, own elements) would
/// otherwise inherit what it left behind — a "cm" without its Q drew the bars of a redaction as thin lines (docs/FEHLERGEDAECHTNIS.md,
/// No. 148). A Q without a q must not be wrapped as it is, though: before q put in front of the content it would restore the state those q
/// saved — the page's initial state — where readers pass over it, and what follows it would be drawn in that state (a line of text lost its
/// font size and slid under a redaction bar; No. 383). <see cref="WithoutStrayRestores"/> leaves such Q out first, which changes nothing.
/// </summary>
internal static class StateBalance
{
    /// <summary>
    /// The content streams without each Q that restores nothing — one with no q open before it, counted over the streams in order, as
    /// readers pass over it. Its operands go with it (they belong to no other operator).
    /// </summary>
    /// <param name="streams">The decoded content streams.</param>
    /// <returns>Per stream the new content, or null where it has no such Q.</returns>
    public static byte[]?[] WithoutStrayRestores(IReadOnlyList<byte[]> streams)
    {
        var result = new byte[]?[streams.Count];
        var depth = 0;
        for (var s = 0; s < streams.Count; s++)
        {
            var data = streams[s];
            var parser = new ContentParser(data);
            List<(int Start, int End)>? stray = null;
            var before = 0;
            while (parser.Next() is { } op)
            {
                if (op == "q") depth++;
                else if (op == "Q" && depth > 0) depth--;
                else if (op == "Q") (stray ??= []).Add((before, parser.Position));
                before = parser.Position;
            }
            if (stray is null) continue;
            var output = new MemoryStream(data.Length);
            var from = 0;
            foreach (var (start, end) in stray)
            {
                output.Write(data, from, start - from);
                output.WriteByte((byte)' ');   // the tokens on either side stay apart
                from = end;
            }
            output.Write(data, from, data.Length - from);
            result[s] = output.ToArray();
        }
        return result;
    }

    /// <summary>Counts q and Q over the content streams in order.</summary>
    /// <param name="streams">The decoded content streams.</param>
    /// <returns>q to put before (at least 1) and Q to put after.</returns>
    public static (int Open, int Close) Of(IEnumerable<byte[]> streams)
    {
        int depth = 0, lowest = 0;
        foreach (var data in streams)
        {
            var parser = new ContentParser(data);
            while (parser.Next() is { } op)
            {
                if (op == "q") depth++;
                else if (op == "Q") lowest = Math.Min(lowest, --depth);
            }
        }
        var open = Math.Clamp(1 - lowest, 1, 4096);
        return (open, Math.Clamp(open + depth, 1, 8192));
    }
}
