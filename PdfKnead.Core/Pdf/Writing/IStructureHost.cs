using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// What <see cref="StructurePlan"/> needs of the writer: the structure tree of the main source as the copy has it so far, copies of its
/// objects it may change, numbers and the queue for new objects, and the pages of the copy (docs/ABLAUFPLAN.md, 6.2 step 4).
/// </summary>
internal interface IStructureHost
{
    /// <summary>
    /// The structure tree root of the copy: the reference to that of the main source, or — a tree made for the files merged into an untagged
    /// copy (docs/ABLAUFPLAN.md, 6.5 c) — the number of a new one (<see cref="OutputReference"/>), whose dictionary is <see cref="SourceRoot"/>.
    /// </summary>
    PdfObject RootReference { get; }

    /// <summary>The structure tree root as read (it resolves its references itself: role map, namespaces).</summary>
    PdfDictionary SourceRoot { get; }

    /// <summary>Whether the copy is written as PDF 2.0.</summary>
    bool Pdf20 { get; }

    /// <summary>The number of pages of the copy.</summary>
    int PageCount { get; }

    /// <summary>The object behind a value of the main source as the copy has it: a replaced object as replaced; anything else as it is.</summary>
    PdfObject? Resolve(PdfObject? value);

    /// <summary>A dictionary of the main source the copy may change: its replacement if there is one, else a copy put in its place.</summary>
    PdfDictionary Writable(PdfReference reference);

    /// <summary>An object of the main source the copy does not write (an element taken out of the tree); references to it become null.</summary>
    void Drop(PdfReference reference);

    /// <summary>A new object number of the copy.</summary>
    int NewNumber();

    /// <summary>A value of the main source as an object of the copy: references renumbered (to objects not written: null).</summary>
    PdfObject Rebase(PdfObject value);

    /// <summary>Writes a new object of the copy (its references already of the copy).</summary>
    void Enqueue(int number, PdfObject obj);

    /// <summary>The object number of a page of the copy.</summary>
    int PageNumber(int page);

    /// <summary>The key of a page into the parent tree of the main source (/StructParents); null for none or a page of another file.</summary>
    int? StructParents(int page);

    /// <summary>The page of the copy (index) a /Pg of the main source or of the copy names; null if it names none written.</summary>
    int? PageIndexOf(PdfObject? pg);

    /// <summary>The decoded content of a page and /Properties of its resources.</summary>
    (byte[] Content, PdfDictionary? Properties) PageContent(int page);

    /// <summary>What identifies the content of a page: pages that share their content stream share it (they share the MCIDs).</summary>
    object ContentKey(int page);
}
