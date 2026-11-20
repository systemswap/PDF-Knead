using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Parsing;

/// <summary>
/// Reused collection lists for the parsers (one set per thread): the entries of an array or dictionary are
/// collected here and the object is then created with exactly the right size, instead of a list that grows in
/// steps for each of the thousands of small objects in a file.
/// </summary>
internal static class ParseScratch
{
    [ThreadStatic] private static Stack<List<PdfObject>>? _objects;
    [ThreadStatic] private static Stack<List<(PdfName, PdfObject)>>? _entries;

    public static List<PdfObject> RentObjects() => (_objects ??= new()).TryPop(out var list) ? list : new List<PdfObject>(16);

    public static void Return(List<PdfObject> list)
    {
        list.Clear();
        if (list.Capacity <= 4096) (_objects ??= new()).Push(list);
    }

    public static List<(PdfName, PdfObject)> RentEntries() => (_entries ??= new()).TryPop(out var list) ? list : new List<(PdfName, PdfObject)>(16);

    public static void Return(List<(PdfName, PdfObject)> list)
    {
        list.Clear();
        if (list.Capacity <= 4096) (_entries ??= new()).Push(list);
    }

    /// <summary>Array with exactly the right size.</summary>
    public static PdfArray ToArray(List<PdfObject> items) => PdfArray.CopyOf(items);

    /// <summary>Dictionary with exactly the right size (for duplicate keys, the last one wins).</summary>
    public static PdfDictionary ToDictionary(List<(PdfName, PdfObject)> entries)
    {
        var dict = new PdfDictionary(entries.Count);
        foreach (var (key, value) in entries) dict.Set(key, value);
        return dict;
    }
}
