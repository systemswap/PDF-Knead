using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Signatures;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// A visible signature in a tagged file (docs/ABLAUFPLAN.md, 6.4 step 5, J4; ISO 14289-1, 7.18.1, 7.18.4; ISO 14289-2, 8.9.2.1, 8.10.1;
/// docs/FEHLERGEDAECHTNIS.md, No. 635): the widget of the new signature field gets a Form element with an object reference and a
/// /StructParent, the page /Tabs /S, the widget /TU and /Contents „Digitale Signatur“ without the signer — in the incremental update, the
/// signature valid. Before: no element, no /StructParent; every visibly signed tagged file lost PDF/UA and kept the identification.
/// </summary>
public sealed class SignatureStructureTests
{
    private static readonly Lazy<SigningCertificate> Own = new(() =>
        SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"struktur-{Guid.NewGuid():N}.p12")));

    private static readonly DateTimeOffset When = new(2026, 11, 8, 10, 0, 0, TimeSpan.FromHours(1));

    private static InkElement Signature(double left, double bottom) => new()
    {
        Rect = new PdfRectangle(left, bottom, left + 180, bottom + 60),
        Strokes = [[0, 20, 40, 50, 90, 10, 140, 45, 175, 25]],
        Digital = new DigitalSigning("Max Mustermann · 08.11.2026 10:00"),
    };

    /// <summary>One page, a paragraph (8, MCID 0) under a Document (7); <paramref name="typed"/> false: the elements without /Type.</summary>
    private static byte[] Tagged(bool typed = true) => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 6 0 R/MarkInfo<</Marked true>>/Lang(de-DE)>>",
        [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>/StructParents 0>>",
        [4] = ("", "/P <</MCID 0>> BDC BT /F1 12 Tf 72 760 Td (Vertrag zwischen Max Mustermann und Erika Mustermann) Tj ET EMC"),
        [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
        [6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1>>",
        [7] = typed ? "<</Type/StructElem/S/Document/P 6 0 R/K 8 0 R>>" : "<</S/Document/P 6 0 R/K 8 0 R>>",
        [8] = typed ? "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 0>>" : "<</S/P/P 7 0 R/Pg 3 0 R/K 0>>",
        [15] = "<</Nums[0[8 0 R]]>>",
    });

    private static PdfDictionary SignatureWidget(PdfDocument doc) =>
        Enumerable.Range(0, doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Count)
            .Select(i => (PdfDictionary)doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(i))
            .Last(a => a.GetName(PdfName.Get("FT"))?.Value == "Sig");

    private static PdfDictionary ElementOf(PdfDocument doc, PdfDictionary annot) =>
        (PdfDictionary)NumberTree.Value(doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.GetDictionary(PdfName.Get("ParentTree"))!,
            annot.GetInteger(PdfName.Get("StructParent"))!.Value)!;

    /// <summary>Signed visibly: Form element with an object reference to the widget, /StructParent 1, /Tabs /S, /TU and /Contents; the signature valid.</summary>
    [Fact]
    public void TaggedDocument_VisibleSignature_InAFormElement()
    {
        using var doc = PdfDocument.Open(Tagged());
        var signed = PdfSigner.Sign(doc, 0, Signature(72, 600), Own.Value, When);
        using var reopened = PdfDocument.Open(signed);
        var widget = SignatureWidget(reopened);
        Assert.Equal(1, widget.GetInteger(PdfName.Get("StructParent")));
        var form = ElementOf(reopened, widget);
        Assert.Equal("Form", form.GetName(PdfName.Get("S"))!.Value);
        Assert.Same(widget, ((PdfDictionary)form.Get(PdfName.Get("K"))!).Get(PdfName.Get("Obj")));
        Assert.Equal("S", reopened.Pages[0].Dictionary.GetName(PdfName.Get("Tabs"))!.Value);
        Assert.Equal("Digitale Signatur", widget.GetString(PdfName.Get("TU"))!.ToText());
        Assert.Equal("Digitale Signatur", widget.GetString(PdfName.Contents)!.ToText());
        var check = Assert.Single(SignatureVerifier.Verify(reopened, null, [], CancellationToken.None));
        Assert.Equal(SignatureOutcome.ValidIdentityUnknown, check.Outcome);
        Assert.Equal(2, reopened.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.GetInteger(PdfName.Get("ParentTreeNextKey")));
        StructureAssert.Clean(reopened);
    }

    /// <summary>
    /// Signed a second time: the second widget gets its Form element too, and the first signature stays valid — the change check counts the
    /// structure added for a new field as filling in and signing (docs/FEHLERGEDAECHTNIS.md, No. 637; before No. 637 the parent tree changed
    /// counted as another change, and the first signature appeared changed).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]   // elements without /Type (optional, ISO 32000-2, 14.7.2): before the first signature appeared changed (No. 719)
    public void SignedTwice_FirstStaysValid_BothInTheTree(bool typed)
    {
        using var doc = PdfDocument.Open(Tagged(typed));
        var once = PdfSigner.Sign(doc, 0, Signature(72, 600), Own.Value, When);
        using var reopened = PdfDocument.Open(once);
        var twice = PdfSigner.Sign(reopened, 0, Signature(300, 600), Own.Value, When.AddHours(1));
        using var last = PdfDocument.Open(twice);
        var checks = SignatureVerifier.Verify(last, null, [], CancellationToken.None);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.Equal(SignatureOutcome.ValidIdentityUnknown, c.Outcome));
        var sigWidgets = Enumerable.Range(0, last.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Count)
            .Select(i => (PdfDictionary)last.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(i)).Where(a => a.GetName(PdfName.Get("FT"))?.Value == "Sig").ToList();
        Assert.Equal(2, sigWidgets.Select(w => w.GetInteger(PdfName.Get("StructParent"))).Distinct().Count());
        Assert.All(sigWidgets, w => Assert.Equal("Form", ElementOf(last, w).GetName(PdfName.Get("S"))!.Value));
        Assert.All(checks, c => Assert.NotEqual(LaterChanges.Other, c.Later));
        StructureAssert.Clean(last);
    }

    /// <summary>
    /// The rule of the change check for the parent tree (docs/FEHLERGEDAECHTNIS.md, No. 637), on a revision written by hand after the first
    /// signature: a pair added that names a structure element is filling in and signing; a pair changed, or one that names anything but an
    /// element, stays a change of what was signed.
    /// </summary>
    [Theory]
    [InlineData("neues Element", false)]
    [InlineData("Paar geändert", true)]
    [InlineData("Paar auf eine Anmerkung", true)]
    public void ParentTreeRule_OnlyGrowthByElements(string change, bool changedAfterSigning)
    {
        using var doc = PdfDocument.Open(Tagged());
        var signed = PdfSigner.Sign(doc, 0, Signature(72, 600), Own.Value, When);
        using var reopened = PdfDocument.Open(signed);
        var update = new Core.Pdf.Writing.IncrementalUpdate(reopened);
        var root = reopened.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!;
        var treeRef = (PdfReference)root.GetRaw(PdfName.Get("ParentTree"))!;
        var nums = ((PdfDictionary)reopened.Resolve(treeRef.Id)).GetArray(PdfName.Get("Nums"))!;
        var entries = Enumerable.Range(0, nums.Count).Select(i => nums[i]).ToList();
        switch (change)
        {
            case "neues Element":
                var element = TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("Form")), ("P", root.GetRaw(PdfName.Get("K"))!));
                entries.AddRange([PdfInteger.Of(9), update.Add(element)]);
                break;
            case "Paar geändert":
                entries[1] = new PdfArray([PdfNull.Instance]);
                break;
            case "Paar auf eine Anmerkung":
                entries.AddRange([PdfInteger.Of(9), update.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Square")), ("Rect", TestPdf.Box(0, 0, 10, 10))))]);
                break;
        }
        update.Replace(treeRef.Id, TestPdf.Dict(("Nums", new PdfArray(entries))));
        using var output = new MemoryStream();
        update.WriteTo(output);
        using var appended = PdfDocument.Open(output.ToArray());
        var check = Assert.Single(SignatureVerifier.Verify(appended, null, [], CancellationToken.None));
        Assert.Equal(changedAfterSigning, check.Later == LaterChanges.Other);
    }

    /// <summary>An untagged file: no /StructParent, no /TU from the structure step.</summary>
    [Fact]
    public void UntaggedDocument_Unchanged()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        using var reopened = PdfDocument.Open(PdfSigner.Sign(doc, 0, Signature(72, 600), Own.Value, When));
        var widget = SignatureWidget(reopened);
        Assert.False(widget.ContainsKey(PdfName.Get("StructParent")));
        Assert.False(reopened.Pages[0].Dictionary.ContainsKey(PdfName.Get("Tabs")));
    }
}
