using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The plan for changes to the structure tree while the writer writes a copy (<see cref="StructurePlan"/>; docs/ABLAUFPLAN.md, 6.2 steps 4
/// to 6): new elements at a place the containment rules allow, marked content and object references, flattening, one parent tree built anew.
/// Tested against a host over a file as read, where the copy keeps every object number (a reference of the source becomes an
/// <see cref="OutputReference"/> of the same number).
/// </summary>
public sealed class StructurePlanTests
{
    /// <summary>
    /// Two pages (3, 11). Page 1 marks MCID 0 and 2 (paragraph 8) and has a link annotation (20, /StructParent 5, element 21); page 2 marks
    /// MCID 0 (paragraph 9). Document 7 under the root 6; the parent tree pairs 0, 1 and 5, /ParentTreeNextKey 1 — wrong, as files have it.
    /// <paramref name="pdf20Namespace"/>: the root names the namespace for PDF 2.0 (object 30) in /Namespaces. <paramref name="noDocument"/>:
    /// the root holds a Sect instead of a Document.
    /// </summary>
    private static PdfDocument Tagged(bool pdf20Namespace = false, bool noDocument = false)
    {
        var objects = new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 6 0 R/MarkInfo<</Marked true>>/Lang(de-DE)>>",
            [2] = "<</Type/Pages/Kids[3 0 R 11 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>/StructParents 0/Annots[20 0 R]>>",
            [4] = ("", "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET EMC /P <</MCID 2>> BDC BT /F1 12 Tf 72 680 Td (Hauptstr. 1) Tj ET EMC"),
            [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            [6] = $"<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1{(pdf20Namespace ? "/Namespaces[30 0 R]" : "")}>>",
            [7] = $"<</Type/StructElem/S/{(noDocument ? "Sect" : "Document")}/P 6 0 R/K[8 0 R 9 0 R 21 0 R]>>",
            [8] = "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K[0 2]>>",
            [9] = "<</Type/StructElem/S/P/P 7 0 R/Pg 11 0 R/K 0>>",
            [11] = "<</Type/Page/Parent 2 0 R/Contents 12 0 R/Resources<</Font<</F1 5 0 R>>>>/StructParents 1>>",
            [12] = ("", "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Erika Mustermann) Tj ET EMC"),
            [15] = "<</Nums[0[8 0 R null 8 0 R] 1[9 0 R] 5 21 0 R]>>",
            [20] = "<</Type/Annot/Subtype/Link/Rect[72 600 200 620]/StructParent 5/Border[0 0 0]>>",
            [21] = "<</Type/StructElem/S/Link/P 7 0 R/Pg 3 0 R/K<</Type/OBJR/Obj 20 0 R/Pg 3 0 R>>>>",
        };
        if (pdf20Namespace) objects[30] = "<</Type/Namespace/NS(http://iso.org/pdf2/ssn)>>";
        return PdfDocument.Open(CraftedFileTests.Build(objects));
    }

    /// <summary>The writer as the plan sees it, over a file as read: every object keeps its number in the copy.</summary>
    private sealed class Host(PdfDocument doc, bool pdf20 = false) : IStructureHost
    {
        public Dictionary<int, PdfObject> Overrides { get; } = [];
        public Dictionary<int, PdfObject> Written { get; } = [];
        private int _next = 1000;

        private PdfReference Reference => (PdfReference)doc.Catalog!.GetRaw(PdfName.Get("StructTreeRoot"))!;
        public PdfObject RootReference => Reference;
        public PdfDictionary SourceRoot => (PdfDictionary)doc.Resolve(Reference.Id);
        public bool Pdf20 => pdf20;
        public int PageCount => doc.Pages.Count;

        public PdfObject? Resolve(PdfObject? value) => value switch
        {
            PdfReference r => Overrides.TryGetValue(r.Id.Number, out var o) ? o : doc.Resolve(r.Id),
            OutputReference o when Written.TryGetValue(o.Number, out var w) => w,
            OutputReference o when Overrides.TryGetValue(o.Number, out var w) => w,
            OutputReference o => doc.Resolve(new PdfObjectId(o.Number, 0)),
            _ => value,
        };

        public PdfDictionary Writable(PdfReference reference)
        {
            if (Overrides.TryGetValue(reference.Id.Number, out var o) && o is PdfDictionary own) return own;
            var copy = new PdfDictionary();
            foreach (var (key, value) in ((PdfDictionary)doc.Resolve(reference.Id)).Entries) copy.Set(key, value);
            Overrides[reference.Id.Number] = copy;
            return copy;
        }

        public int NewNumber() => _next++;

        public HashSet<int> Dropped { get; } = [];

        public void Drop(PdfReference reference) => Dropped.Add(reference.Id.Number);

        public PdfObject Rebase(PdfObject value) => value switch
        {
            PdfReference r => new OutputReference(r.Id.Number),
            PdfArray a => new PdfArray(a.Select(Rebase)),
            PdfDictionary d => Copy(d),
            _ => value,
        };

        private PdfDictionary Copy(PdfDictionary d)
        {
            var copy = new PdfDictionary();
            foreach (var (key, value) in d.Entries) copy.Set(key, Rebase(value));
            return copy;
        }

        public void Enqueue(int number, PdfObject obj) => Written[number] = obj;
        public int PageNumber(int page) => doc.Pages[page].Id!.Value.Number;

        public int? PageIndexOf(PdfObject? pg)
        {
            var number = pg switch { PdfReference r => r.Id.Number, OutputReference o => o.Number, _ => -1 };
            for (var i = 0; i < doc.Pages.Count; i++)
                if (doc.Pages[i].Id?.Number == number) return i;
            return null;
        }

        public int? StructParents(int page) =>
            doc.Pages[page].Dictionary.Get(PdfName.Get("StructParents")) is PdfInteger key ? (int)key.Value : null;

        public (byte[] Content, PdfDictionary? Properties) PageContent(int page)
        {
            using var content = PageInterpreter.PageContent(doc, doc.Pages[page].Dictionary);
            return (content.Span.ToArray(), null);
        }

        public object ContentKey(int page) => page;

        /// <summary>The root as the copy writes it.</summary>
        public PdfDictionary Root => (PdfDictionary)Overrides[Reference.Id.Number];

        /// <summary>
        /// The pairs of the parent tree written anew (through /Kids); an array written as an object of its own (7.9.6 sentence 5,
        /// docs/FEHLERGEDAECHTNIS.md, No. 779) as that array — never given directly.
        /// </summary>
        public List<(long Key, PdfObject Value)> Pairs()
        {
            var pairs = new List<(long, PdfObject)>();
            void Walk(PdfDictionary node)
            {
                if (node.GetRaw(PdfName.Get("Nums")) is PdfArray nums)
                    for (var i = 0; i + 1 < nums.Count; i += 2)
                    {
                        var value = nums[i + 1];
                        Assert.IsNotType<PdfArray>(value);
                        if (value is OutputReference o && Written.TryGetValue(o.Number, out var written) && written is PdfArray array) value = array;
                        pairs.Add((((PdfInteger)nums[i]).Value, value));
                    }
                if (node.GetRaw(PdfName.Get("Kids")) is PdfArray kids)
                    foreach (var kid in kids) Walk((PdfDictionary)Resolve(kid)!);
            }
            Walk((PdfDictionary)Resolve(Root.GetRaw(PdfName.Get("ParentTree")))!);
            return pairs;
        }
    }

    private static PdfReference Ref(int number) => new(new PdfObjectId(number, 0));

    private static int NumberOf(PdfObject? handle) => handle switch
    {
        OutputReference o => o.Number,
        PdfReference r => r.Id.Number,
        _ => -1,
    };

    /// <summary>
    /// One parent tree built anew: sorted, the pairs kept, the new MCID of page 1 in its array naming the new element, a new object reference
    /// with a key above every key — /ParentTreeNextKey above them all (the file said 1, its largest key is 5).
    /// </summary>
    [Fact]
    public void ParentTreeRebuilt_SortedAndNextKeyAbove()
    {
        using var doc = Tagged();
        var host = new Host(doc);
        var plan = StructurePlan.For(host)!;
        var figure = plan.NewElement("Figure", Ref(8), 0, alt: "Stempel")!;
        var mcid = plan.NextMcid(0);
        Assert.Equal(3, mcid);
        plan.AddMarkedContent(figure, 0, mcid);
        var note = plan.NewElement("Annot", Ref(9), 1)!;
        var key = plan.AddObjectReference(note, 1, new OutputReference(500));
        Assert.Equal(6, key);
        plan.Finish();

        var pairs = host.Pairs();
        Assert.Equal([0L, 1L, 5L, 6L], pairs.Select(p => p.Key));
        var page1 = (PdfArray)pairs[0].Value;
        Assert.Equal(4, page1.Count);
        Assert.Equal(8, NumberOf(page1[0]));
        Assert.IsType<PdfNull>(page1[1]);
        Assert.Equal(NumberOf(figure), NumberOf(page1[3]));
        Assert.Equal(NumberOf(note), NumberOf(pairs[3].Value));
        Assert.Equal(7, host.Root.GetInteger(PdfName.Get("ParentTreeNextKey")));
    }

    /// <summary>
    /// A new figure beside a paragraph becomes its sibling right after it (P may hold a Figure — so it goes into the paragraph at its end):
    /// /P names the parent, /Pg the page, /Alt the text; the parent's /K has it, its MCID stands in /K directly (same page).
    /// </summary>
    [Fact]
    public void NewElement_AtThePlaceTheRulesAllow_WithParentAndPage()
    {
        using var doc = Tagged();
        var host = new Host(doc);
        var plan = StructurePlan.For(host)!;
        var paragraph = plan.NewElement("P", Ref(8), 0)!;
        plan.AddMarkedContent(paragraph, 0, plan.NextMcid(0));
        plan.Finish();
        var document = (PdfDictionary)host.Overrides[7];
        var kids = (PdfArray)document.GetRaw(PdfName.Get("K"))!;
        Assert.Equal([8, NumberOf(paragraph), 9, 21], kids.Select(NumberOf));
        var written = (PdfDictionary)host.Written[NumberOf(paragraph)];
        Assert.Equal(7, NumberOf(written.GetRaw(PdfName.Get("P"))));
        Assert.Equal(3, NumberOf(written.GetRaw(PdfName.Get("Pg"))));
        Assert.Equal([3L], ((PdfArray)written.GetRaw(PdfName.Get("K"))!).Select(k => ((PdfInteger)k).Value));
    }

    /// <summary>Marked content on a page other than the element's /Pg: a marked-content reference with /Pg (ISO 32000-2, 14.7.5.2).</summary>
    [Fact]
    public void MarkedContentOnAnotherPage_IsAReference()
    {
        using var doc = Tagged();
        var host = new Host(doc);
        var plan = StructurePlan.For(host)!;
        plan.AddMarkedContent(Ref(8), 1, plan.NextMcid(1));
        plan.Finish();
        var kids = (PdfArray)((PdfDictionary)host.Overrides[8]).GetRaw(PdfName.Get("K"))!;
        var reference = (PdfDictionary)kids[^1];
        Assert.Equal("MCR", reference.GetName(PdfName.Type)!.Value);
        Assert.Equal(11, NumberOf(reference.GetRaw(PdfName.Get("Pg"))));
        Assert.Equal(1, reference.GetInteger(PdfName.Get("MCID")));
    }

    /// <summary>The next MCID is above what the content marks and what the page's array holds, and every call gives a new one.</summary>
    [Fact]
    public void NextMcid_AboveContentAndArray()
    {
        using var doc = Tagged();
        var plan = StructurePlan.For(new Host(doc))!;
        Assert.Equal(3, plan.NextMcid(0));
        Assert.Equal(4, plan.NextMcid(0));
        Assert.Equal(1, plan.NextMcid(1));
    }

    /// <summary>
    /// Flattening: the object reference to the link becomes the marked content; the link's key goes from the parent tree, the page's array
    /// names the element at the MCID.
    /// </summary>
    [Fact]
    public void ReplaceObjectReference_KeyGoesMcidComes()
    {
        using var doc = Tagged();
        var host = new Host(doc);
        var plan = StructurePlan.For(host)!;
        var mcid = plan.NextMcid(0);
        Assert.True(plan.ReplaceObjectReference(Ref(21), Ref(20), 0, mcid));
        plan.Finish();
        Assert.Equal([0L, 1L], host.Pairs().Select(p => p.Key));
        Assert.Equal([3L], ((PdfArray)((PdfDictionary)host.Overrides[21]).GetRaw(PdfName.Get("K"))!).Select(k => ((PdfInteger)k).Value));
        Assert.Equal(21, NumberOf(((PdfArray)host.Pairs()[0].Value)[mcid]));
    }

    /// <summary>Without a Document under the root, a new element with no place goes into a Document made for it that takes the root's kids.</summary>
    [Fact]
    public void NoDocument_OneIsMadeAboveTheKids()
    {
        using var doc = Tagged(noDocument: true);
        var host = new Host(doc);
        var plan = StructurePlan.For(host)!;
        var paragraph = plan.NewElement("P", null, 0)!;
        plan.Finish();
        var rootKids = (PdfArray)host.Root.GetRaw(PdfName.Get("K"))!;
        Assert.Single(rootKids);
        var document = (PdfDictionary)host.Written[NumberOf(rootKids[0])];
        Assert.Equal("Document", document.GetName(PdfName.Get("S"))!.Value);
        Assert.Equal([7, NumberOf(paragraph)], ((PdfArray)document.GetRaw(PdfName.Get("K"))!).Select(NumberOf));
        Assert.Equal(NumberOf(rootKids[0]), NumberOf(((PdfDictionary)host.Overrides[7]).GetRaw(PdfName.Get("P"))));
    }

    /// <summary>
    /// A role map that maps Document to a type of its own (against the rules): the element named Document is still the top Document — no new
    /// one around the kids (before: one more Document on every save; docs/FEHLERGEDAECHTNIS.md, No. 633).
    /// </summary>
    [Fact]
    public void RemappedDocument_FoundByItsName()
    {
        using var doc = PdfDocument.Open(CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 6 0 R/MarkInfo<</Marked true>>>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>/StructParents 0>>",
            [4] = ("", "/P <</MCID 0>> BDC 0 0 m 1 1 l S EMC"),
            [6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1/RoleMap<</Document/Book>>>>",
            [7] = "<</Type/StructElem/S/Document/P 6 0 R/K 8 0 R>>",
            [8] = "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 0>>",
            [15] = "<</Nums[0[8 0 R]]>>",
        }));
        var host = new Host(doc);
        var plan = StructurePlan.For(host)!;
        var added = plan.NewElement("Annot", null, 0)!;
        plan.Finish();
        Assert.Equal(7, NumberOf(host.Root.GetRaw(PdfName.Get("K"))));   // the root keeps its one kid, no Document around it
        Assert.Equal([8, NumberOf(added)], ((PdfArray)((PdfDictionary)host.Overrides[7]).GetRaw(PdfName.Get("K"))!).Select(NumberOf));
    }

    /// <summary>
    /// Written as 2.0 into a tree with the namespace for PDF 2.0: new elements of that namespace name it with /NS; a 1.7 tree gets no 2.0 type
    /// (ISO/TS 32005, 5.3), a 2.0 tree no type only 1.7 has (Note: FENote).
    /// </summary>
    [Fact]
    public void Namespaces_RulesForNewElements()
    {
        using (var doc = Tagged(pdf20Namespace: true))
        {
            var host = new Host(doc, pdf20: true);
            var plan = StructurePlan.For(host)!;
            Assert.True(plan.Pdf20Types);
            var aside = plan.NewElement("Aside", Ref(9), 1)!;
            plan.Finish();
            Assert.Equal(30, NumberOf(((PdfDictionary)host.Written[NumberOf(aside)]).GetRaw(PdfName.Get("NS"))));
            Assert.Throws<ArgumentException>(() => plan.NewElement("Note", Ref(9), 1));
        }
        using (var doc = Tagged())
        {
            var plan = StructurePlan.For(new Host(doc, pdf20: true))!;
            Assert.False(plan.Pdf20Types);
            Assert.Throws<ArgumentException>(() => plan.NewElement("Aside", Ref(9), 1));
            Assert.Throws<ArgumentException>(() => plan.NewElement("Sidebar", Ref(9), 1));
        }
    }

    /// <summary>Nothing changed: nothing written, the root not replaced.</summary>
    [Fact]
    public void NothingChanged_NothingWritten()
    {
        using var doc = Tagged();
        var host = new Host(doc);
        StructurePlan.For(host)!.Finish();
        Assert.Empty(host.Written);
        Assert.Empty(host.Overrides);
    }
}
