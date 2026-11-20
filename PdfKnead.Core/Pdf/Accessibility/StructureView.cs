using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>
/// A structure tree as it stands while it is being changed: elements by handle (the reference to them, or the dictionary of a direct one),
/// their parents, their kids and their standard types, read through one resolver. The writer passes a resolver that sees the objects it
/// replaced and the elements it adds; a reader passes the document's. <see cref="Containment.PlaceFor"/> finds places in it.
/// </summary>
internal sealed class StructureView
{
    private static readonly PdfName S = PdfName.Get("S"), P = PdfName.Get("P"), K = PdfName.Get("K");

    private readonly Func<PdfObject?, PdfObject?> _resolve;

    /// <summary>A view through a resolver.</summary>
    /// <param name="root">The structure tree root as read (its role map; the elements' namespaces).</param>
    /// <param name="resolve">Resolves a handle (a reference) to the object as it stands now; anything else comes back as it is.</param>
    public StructureView(PdfDictionary root, Func<PdfObject?, PdfObject?> resolve)
    {
        Root = root;
        _resolve = resolve;
    }

    /// <summary>A view of a structure tree as a document has it.</summary>
    public static StructureView Of(PdfDocument doc, PdfDictionary root) => new(root, o => o is PdfReference r ? doc.Resolve(r.Id) : o);

    /// <summary>The structure tree root.</summary>
    public PdfDictionary Root { get; }

    /// <summary>The object behind a handle as it stands now.</summary>
    public PdfObject? Resolve(PdfObject? handle) => _resolve(handle);

    /// <summary>The structure element behind a handle; null for the root, content items (MCID, MCR, OBJR) and anything else.</summary>
    public PdfDictionary? Element(PdfObject? handle) =>
        _resolve(handle) is PdfDictionary d && !ReferenceEquals(d, Root) && _resolve(d.GetRaw(S)) is PdfName &&
        d.GetName(PdfName.Type)?.Value is not ("StructTreeRoot" or "MCR" or "OBJR")
            ? d
            : null;

    /// <summary>Whether a handle is the structure tree root.</summary>
    public bool IsRoot(PdfObject? handle) =>
        _resolve(handle) is PdfDictionary d && (ReferenceEquals(d, Root) || d.GetName(PdfName.Type)?.Value == "StructTreeRoot");

    /// <summary>The handle of an element's parent (/P), null if it has none.</summary>
    public PdfObject? Parent(PdfObject handle) => Element(handle)?.GetRaw(P);

    /// <summary>The entries of an element's /K as they stand (MCIDs, content items, elements), at most 100 000.</summary>
    public IReadOnlyList<PdfObject> Items(PdfObject handle)
    {
        var dict = Element(handle) ?? (IsRoot(handle) ? _resolve(handle) as PdfDictionary : null);
        return _resolve(dict?.GetRaw(K)) switch
        {
            null or PdfNull => [],
            PdfArray array => [.. array.Take(100_000)],
            var one => [dict!.GetRaw(K)!],
        };
    }

    /// <summary>The handles of an element's kids that are elements.</summary>
    public IEnumerable<PdfObject> Kids(PdfObject handle) => Items(handle).Where(i => Element(i) is not null);

    /// <summary>Whether an element holds content items directly (an MCID, an MCR or an OBJR among its /K).</summary>
    public bool HoldsContent(PdfObject handle) =>
        Items(handle).Any(i => _resolve(i) is PdfInteger || _resolve(i) is PdfDictionary d && d.GetName(PdfName.Type)?.Value is "MCR" or "OBJR");

    /// <summary>The standard type of an element (<see cref="StandardStructure.Resolve"/>); null if it stands for none.</summary>
    public string? TypeOf(PdfObject handle) => Element(handle) is { } e ? StandardStructure.Resolve(e, Root, _resolve)?.Name : null;

    /// <summary>Whether two handles name the same element: the same object number, or the same direct dictionary.</summary>
    public static bool Same(PdfObject? a, PdfObject? b) => a switch
    {
        PdfReference ra => b is PdfReference rb && ra.Id.Number == rb.Id.Number,
        Writing.OutputReference oa => b is Writing.OutputReference ob && oa.Number == ob.Number,
        _ => a is not null && ReferenceEquals(a, b),
    };
}
