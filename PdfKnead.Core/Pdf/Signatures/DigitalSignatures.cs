using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>A digital signature found in a signature field of the document (ISO 32000-2:2020, section 12.8).</summary>
/// <param name="FieldName">Partial name of the signature field (<c>/T</c>), or an empty string.</param>
/// <param name="ByteRange">The signed byte ranges (<c>/ByteRange</c>): offset and length of the part before and after <c>/Contents</c>.</param>
/// <param name="Intact">
/// Whether the byte ranges still fit the file: they start at 0, end inside the file, and leave out exactly the hexadecimal
/// <c>/Contents</c> string. A file that was rewritten after signing no longer fits, so its signature counts as already broken.
/// This is a structural check only; the cryptographic check of the signature is separate (<see cref="SignatureVerifier"/>).
/// </param>
/// <param name="Field">The signature field (merged with its widget, if so).</param>
/// <param name="Signature">The signature dictionary (<c>/V</c> of the field).</param>
/// <param name="FullName">Full name of the field (the partial names up the field tree, joined with dots).</param>
public sealed record DigitalSignatureEntry(string FieldName, long[] ByteRange, bool Intact, PdfDictionary? Field = null,
    PdfDictionary? Signature = null, string FullName = "");

/// <summary>
/// Finds digital signatures in a document: signature fields (<c>/FT /Sig</c>) whose value is a signature dictionary with
/// <c>/ByteRange</c> and <c>/Contents</c>. Used to warn before a save that would invalidate them, because a full rewrite changes
/// the signed bytes.
/// </summary>
/// <remarks>The field tree is walked with limits (10,000 entries per array, 50,000 fields, 32 levels) against prepared files.</remarks>
public static class DigitalSignatures
{
    private const int MaxPerArray = 10_000, MaxFields = 50_000, MaxDepth = 32;

    private static readonly PdfName AcroForm = PdfName.Get("AcroForm"), Fields = PdfName.Get("Fields"), Kids = PdfName.Get("Kids"),
        FieldType = PdfName.Get("FT"), Value = PdfName.Get("V"), Range = PdfName.Get("ByteRange"),
        Contents = PdfName.Get("Contents"), Title = PdfName.Get("T");

    /// <summary>
    /// Lists the digital signatures of the document in field order. A copy of another — the same byte ranges and the same block — counts once
    /// (thousands of them would otherwise each be checked, docs/FEHLERGEDAECHTNIS.md, No. 107); another block claiming the same byte ranges
    /// stays: it cannot be the block in the file and is shown as such, instead of hiding a forged signature (No. 344).
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <returns>The signatures; empty if there are none.</returns>
    public static IReadOnlyList<DigitalSignatureEntry> Find(PdfDocument doc)
    {
        var found = new List<DigitalSignatureEntry>();
        var blocks = new Dictionary<(long, long, long, long), List<byte[]>>();
        foreach (var (field, signature, full) in SignatureFields(doc))
        {
            if (Entry(doc, field, signature, full) is not { } entry) continue;
            var range = (entry.ByteRange[0], entry.ByteRange[1], entry.ByteRange[2], entry.ByteRange[3]);
            var block = signature.GetString(Contents)?.Bytes ?? [];
            if (!blocks.TryGetValue(range, out var seen)) blocks[range] = seen = [];
            else if (seen.Any(b => b.AsSpan().SequenceEqual(block)) || seen.Count >= 16) continue;
            seen.Add(block);
            found.Add(entry);
        }
        return found;
    }

    /// <summary>
    /// Signature fields whose value is a signature dictionary (<c>/Type /Sig</c> or a <c>/Filter</c>) without usable <c>/ByteRange</c> or
    /// <c>/Contents</c>: not a signature that could be checked, but its look on the page may claim one (the „universal signature forgery“ of
    /// https://pdf-insecurity.org) — the check lists them as broken instead of leaving them out.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <returns>The fields, as entries that are not intact (byte ranges zero).</returns>
    public static IReadOnlyList<DigitalSignatureEntry> Unusable(PdfDocument doc)
    {
        var found = new List<DigitalSignatureEntry>();
        foreach (var (field, signature, full) in SignatureFields(doc))
            if ((signature.IsType(PdfName.Get("Sig")) || signature.ContainsKey(PdfName.Filter)) && Entry(doc, field, signature, full) is null)
                found.Add(new DigitalSignatureEntry(field.GetString(Title)?.ToText() ?? "", new long[4], false, field, signature, full));
        return found;
    }

    /// <summary>
    /// The field type of a node: its own /FT, else the nearest one up the /Parent chain (ISO 32000-2:2020, 12.7.4.1: /FT is inheritable, and a
    /// reader must not limit how far; bounded and safe against cycles like the other walks). Null if none.
    /// </summary>
    /// <param name="node">A field or widget.</param>
    public static PdfName? InheritedFieldType(PdfDictionary node)
    {
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var (current, depth) = (node, 0); current is not null && depth <= MaxDepth && seen.Add(current); depth++)
        {
            if (current.GetName(FieldType) is { } type) return type;
            current = current.GetDictionary(PdfName.Parent);
        }
        return null;
    }

    /// <summary>
    /// The signature fields with a signature dictionary as value, each dictionary once (the same one reached from several fields counts once).
    /// The field type is inherited down to the kids in any depth: a signature in a kid whose parent (or grandparent) is the /FT /Sig field was
    /// not found — not listed, not checked, and the file rewritten without asking (docs/ABLAUFPLAN.md, 2.1, A-7).
    /// </summary>
    private static IEnumerable<(PdfDictionary Field, PdfDictionary Signature, string FullName)> SignatureFields(PdfDocument doc)
    {
        if (doc.Catalog?.GetDictionary(AcroForm)?.GetArray(Fields) is not { } fields) yield break;
        var stack = new Stack<(PdfDictionary Field, int Depth, string Prefix, PdfName? Type)>();
        for (var i = Math.Min(fields.Count, MaxPerArray) - 1; i >= 0; i--)
            if (fields.Get(i) is PdfDictionary field) stack.Push((field, 0, "", null));
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var signatures = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var visited = 0;
        while (stack.Count > 0 && visited++ < MaxFields)
        {
            var (field, depth, prefix, inherited) = stack.Pop();
            if (!seen.Add(field)) continue;   // a field reachable twice (prepared or broken files) counts once
            var part = field.GetString(Title)?.ToText();
            var full = part is null ? prefix : prefix.Length == 0 ? part : $"{prefix}.{part}";
            var type = field.GetName(FieldType) ?? inherited;
            if (type?.Value == "Sig" && field.Get(Value) is PdfDictionary signature && signatures.Add(signature))
                yield return (field, signature, full);
            if (depth < MaxDepth && field.GetArray(Kids) is { } kids)
                for (var i = Math.Min(kids.Count, MaxPerArray) - 1; i >= 0; i--)
                    if (kids.Get(i) is PdfDictionary kid) stack.Push((kid, depth + 1, full, type));
        }
    }

    /// <summary>Counts the signatures that a full rewrite of the file would invalidate.</summary>
    /// <param name="doc">The document.</param>
    /// <returns>The number of intact signatures.</returns>
    public static int CountIntact(PdfDocument doc) => Find(doc).Count(s => s.Intact);

    private static DigitalSignatureEntry? Entry(PdfDocument doc, PdfDictionary field, PdfDictionary signature, string full)
    {
        if (signature.GetArray(Range) is not { Count: 4 } array || signature.Get(Contents) is not PdfString) return null;
        var range = new long[4];
        for (var i = 0; i < 4; i++)
        {
            if (array.Get(i) is not PdfInteger { Value: >= 0 } number) return null;
            range[i] = number.Value;
        }
        var name = field.GetString(Title)?.ToText() ?? "";
        return new DigitalSignatureEntry(name, range, Fits(doc, range), field, signature, full);
    }

    /// <summary>The ranges cover the file from its start and leave out exactly a string from „&lt;“ to „&gt;“.</summary>
    private static bool Fits(PdfDocument doc, long[] range)
    {
        var (start1, length1, start2, length2) = (range[0], range[1], range[2], range[3]);
        if (start1 != 0 || length1 <= 0 || start2 <= length1 || length2 < 0 || start2 > doc.FileSize - length2) return false;
        var open = doc.FileBytes(length1, 1);
        var close = doc.FileBytes(start2 - 1, 1);
        return open.Length == 1 && open[0] == (byte)'<' && close.Length == 1 && close[0] == (byte)'>';
    }
}
