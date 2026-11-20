using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// The content streams of the pages written, as they lie in their file (ISO 32000-2, 7.8.2): page contents, forms, tiling patterns, Type 3
/// glyphs, soft-mask groups and the appearance streams of annotations — found through the resources, without decoding content, within a limit
/// (crafted files). For the writer, which writes the numbers in them in the form of 7.3.3 (<see cref="ContentConformance.CanonicalSyntax"/>;
/// S-A A01, docs/FEHLERGEDAECHTNIS.md, No. 930). Streams in memory are not collected: the program writes them itself, in that form.
/// </summary>
internal sealed class ContentStreams
{
    /// <summary>
    /// How deep resources are followed (a form in a form …): at least as deep as the display draws forms — with 16, forms at depth 17–24
    /// were drawn but kept their malformed numbers in the copy (docs/FEHLERGEDAECHTNIS.md, No. 934).
    /// </summary>
    private const int MaxDepth = Content.PageInterpreter.MaxFormDepth;

    /// <summary>How many dictionaries and streams are looked at in all; a crafted file stops here (what was found stays).</summary>
    private const int MaxVisited = 200_000;

    private static readonly PdfName XObject = PdfName.Get("XObject");
    private static readonly PdfName Pattern = PdfName.Get("Pattern");
    private static readonly PdfName ExtGState = PdfName.Get("ExtGState");
    private static readonly PdfName Font = PdfName.Get("Font");
    private static readonly PdfName SMask = PdfName.Get("SMask");
    private static readonly PdfName Group = PdfName.Get("G");
    private static readonly PdfName CharProcs = PdfName.Get("CharProcs");
    private static readonly PdfName Annots = PdfName.Get("Annots");
    private static readonly PdfName Appearance = PdfName.Get("AP");

    // a stream by its document and the place of its data in the file: a copy of the dictionary (VersionKeys) is another object, the data the same
    private readonly HashSet<(PdfDocument, long)> _streams = [];
    private readonly HashSet<PdfObject> _seen = new(ReferenceEqualityComparer.Instance);

    /// <summary>The number of content streams found.</summary>
    public int Count => _streams.Count;

    /// <summary>Whether the stream is a content stream of a page written.</summary>
    /// <param name="doc">The document it comes from.</param>
    /// <param name="stream">The stream.</param>
    public bool Contains(PdfDocument doc, PdfStream stream) => stream.DataOffset >= 0 && _streams.Contains((doc, stream.DataOffset));

    /// <summary>Adds the content streams of a page: its contents, what its resources draw, the appearances of its annotations.</summary>
    /// <param name="doc">The document of the page.</param>
    /// <param name="page">The page.</param>
    public void AddPage(PdfDocument doc, PdfPage page)
    {
        switch (Resolve(doc, page.Dictionary.GetRaw(PdfName.Contents)))
        {
            case PdfStream single:
                Add(doc, single);
                break;
            case PdfArray parts:
                foreach (var part in parts)
                    if (Resolve(doc, part) is PdfStream stream) Add(doc, stream);
                break;
        }
        Walk(doc, page.Resources, 0);
        if (Resolve(doc, page.Dictionary.GetRaw(Annots)) is PdfArray annotations)
            foreach (var entry in annotations)
            {
                if (_seen.Count >= MaxVisited) return;
                if (Resolve(doc, entry) is not PdfDictionary annotation || Resolve(doc, annotation.GetRaw(Appearance)) is not PdfDictionary appearance) continue;
                foreach (var (_, value) in appearance.Entries)
                    switch (Resolve(doc, value))
                    {
                        case PdfStream stream:
                            Form(doc, stream, 0);
                            break;
                        case PdfDictionary states:
                            // appearances by state (on, off …)
                            foreach (var (_, state) in states.Entries)
                                if (Resolve(doc, state) is PdfStream stateStream) Form(doc, stateStream, 0);
                            break;
                    }
            }
    }

    private void Add(PdfDocument doc, PdfStream stream)
    {
        if (stream.DataOffset >= 0) _streams.Add((doc, stream.DataOffset));
    }

    /// <summary>A stream with content and perhaps resources of its own (form, tiling pattern, group, appearance, glyph).</summary>
    private void Form(PdfDocument doc, PdfStream stream, int depth)
    {
        if (!_seen.Add(stream) || _seen.Count >= MaxVisited) return;
        Add(doc, stream);
        if (depth < MaxDepth) Walk(doc, Resolve(doc, stream.Dictionary.GetRaw(PdfName.Resources)) as PdfDictionary, depth + 1);
    }

    private void Walk(PdfDocument doc, PdfDictionary? resources, int depth)
    {
        if (resources is null || depth > MaxDepth || !_seen.Add(resources) || _seen.Count >= MaxVisited) return;
        if (Resolve(doc, resources.GetRaw(XObject)) is PdfDictionary xobjects)
            foreach (var (_, value) in xobjects.Entries)
                if (Resolve(doc, value) is PdfStream form && form.Dictionary.GetName(PdfName.Subtype)?.Value == "Form") Form(doc, form, depth);
        if (Resolve(doc, resources.GetRaw(Pattern)) is PdfDictionary patterns)
            foreach (var (_, value) in patterns.Entries)
                // a tiling pattern is a stream with content (8.7.3.1); a shading pattern is a dictionary without
                if (Resolve(doc, value) is PdfStream tiling) Form(doc, tiling, depth);
        if (Resolve(doc, resources.GetRaw(ExtGState)) is PdfDictionary states)
            foreach (var (_, value) in states.Entries)
                if (Resolve(doc, value) is PdfDictionary state && Resolve(doc, state.GetRaw(SMask)) is PdfDictionary mask &&
                    Resolve(doc, mask.GetRaw(Group)) is PdfStream group)
                    Form(doc, group, depth);
        if (Resolve(doc, resources.GetRaw(Font)) is PdfDictionary fonts)
            foreach (var (_, value) in fonts.Entries)
            {
                if (Resolve(doc, value) is not PdfDictionary font || font.GetName(PdfName.Subtype)?.Value != "Type3" || !_seen.Add(font)) continue;
                if (Resolve(doc, font.GetRaw(CharProcs)) is PdfDictionary glyphs)
                    foreach (var (_, glyph) in glyphs.Entries)
                    {
                        if (_seen.Count >= MaxVisited) return;
                        if (Resolve(doc, glyph) is PdfStream procedure) Add(doc, procedure);
                    }
                if (depth < MaxDepth) Walk(doc, Resolve(doc, font.GetRaw(PdfName.Resources)) as PdfDictionary, depth + 1);
            }
    }

    private static PdfObject? Resolve(PdfDocument doc, PdfObject? value)
    {
        try
        {
            return value is PdfReference r ? doc.Resolve(r.Id) : value;
        }
        catch (PdfException)
        {
            // referring too deep: nothing there for this walk
            return null;
        }
    }
}
