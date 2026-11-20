using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Later revisions that change nothing shown, as other programs write them — no „changed after signing“ (docs/FEHLERGEDAECHTNIS.md,
/// No. 479, 480; found by comparing with the expectations of the reference's tests). Each rule with its limit: what would change what is
/// shown stays a change.
/// </summary>
public sealed partial class ValidationDataTests
{
    private static PdfReference RootOf(PdfDocument doc) => (PdfReference)doc.Trailer.GetRaw(PdfName.Root)!;

    private static PdfDictionary CatalogCopy(PdfDocument doc) => Copy((PdfDictionary)doc.Resolve(RootOf(doc).Id)!);

    /// <summary>Appends a revision that replaces the catalog with <paramref name="change"/> applied to a copy of it.</summary>
    private static byte[] WithCatalog(byte[] file, Action<PdfDictionary, IncrementalUpdate> change) => Append(file, (doc, update) =>
    {
        var catalog = CatalogCopy(doc);
        change(catalog, update);
        update.Replace(RootOf(doc).Id, catalog);
    });

    private static SignatureCheck OwnSignatureOf(byte[] file) => Verify(file).First(c => !c.IsDocumentTimestamp);

    /// <summary>An object written anew without any difference is no change; the same object with /Count changed is (No. 479).</summary>
    [Fact]
    public void RewrittenUnchanged_NoChange_ChangedCountIs()
    {
        var signed = Signed(TestPdf.Simple(2).Build(), null);
        PdfReference PagesOf(PdfDocument doc) => (PdfReference)((PdfDictionary)doc.Resolve(RootOf(doc).Id)!).GetRaw(PdfName.Pages)!;
        var same = OwnSignatureOf(Append(signed, (doc, update) => update.Replace(PagesOf(doc).Id, Copy((PdfDictionary)doc.Resolve(PagesOf(doc).Id)!))));
        Assert.Equal(SignatureOutcome.ValidIdentityUnknown, same.Outcome);
        Assert.Equal(LaterChanges.None, same.Later);
        var counted = OwnSignatureOf(Append(signed, (doc, update) =>
        {
            var pages = Copy((PdfDictionary)doc.Resolve(PagesOf(doc).Id)!);
            pages.Set(PdfName.Get("Count"), PdfInteger.Of(1));
            update.Replace(PagesOf(doc).Id, pages);
        }));
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, counted.Outcome);
    }

    /// <summary>
    /// A node of the page tree with a writer's mark (/ITXT) added changes nothing shown — filling in; /Rotate added turns every page and is
    /// a change (No. 480).
    /// </summary>
    [Fact]
    public void PageTreeMark_Filling_RotateIsChange()
    {
        var signed = Signed(TestPdf.Simple(2).Build(), null);
        byte[] WithPagesKey(string key, PdfObject value) => Append(signed, (doc, update) =>
        {
            var pagesRef = (PdfReference)((PdfDictionary)doc.Resolve(RootOf(doc).Id)!).GetRaw(PdfName.Pages)!;
            var pages = Copy((PdfDictionary)doc.Resolve(pagesRef.Id)!);
            pages.Set(PdfName.Get(key), value);
            update.Replace(pagesRef.Id, pages);
        });
        var marked = OwnSignatureOf(WithPagesKey("ITXT", new PdfString("5.0.0"u8.ToArray())));
        Assert.True(SignatureTexts.IsValid(marked), string.Join(" | ", marked.LaterDetails));
        Assert.Equal(LaterChanges.FormsAndSignatures, marked.Later);
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, OwnSignatureOf(WithPagesKey("Rotate", PdfInteger.Of(90))).Outcome);
    }

    /// <summary>
    /// Page resources only extended (the looks of new fields need fonts) — filling in; an existing name pointing elsewhere could change what
    /// the signed content draws — a change (No. 480).
    /// </summary>
    [Fact]
    public void PageResourcesAdded_Filling_RedefinedNameIsChange()
    {
        var signed = Signed(TestPdf.Simple(1).Build(), null);
        byte[] WithResources(bool redefine) => Append(signed, (doc, update) =>
        {
            var pageRef = doc.Pages[0].Id!.Value;
            var page = Copy(doc.Pages[0].Dictionary);
            // the page's own resources, starting from those it inherits from the page tree (a page with resources of its own sees no others)
            var resources = Copy(doc.Pages[0].Dictionary.GetDictionary(PdfName.Resources)
                                 ?? doc.Pages[0].Dictionary.GetDictionary(PdfName.Parent)!.GetDictionary(PdfName.Resources)!);
            var fonts = Copy(resources.GetDictionary(PdfName.Get("Font")) ?? new PdfDictionary());
            var font = update.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Courier"))));
            var name = redefine ? fonts.Entries.First().Key : PdfName.Get("Zusatz");
            fonts.Set(name, font);
            resources.Set(PdfName.Get("Font"), fonts);
            page.Set(PdfName.Resources, resources);
            update.Replace(new PdfObjectId(pageRef.Number, pageRef.Generation), page);
        });
        var added = OwnSignatureOf(WithResources(redefine: false));
        Assert.True(SignatureTexts.IsValid(added), string.Join(" | ", added.LaterDetails));
        Assert.Equal(LaterChanges.FormsAndSignatures, added.Later);
        var redefined = OwnSignatureOf(WithResources(redefine: true));
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, redefined.Outcome);
        Assert.Contains(Tr.T("Der Inhalt von Seite {0} wurde geändert.", 1), redefined.LaterDetails);
    }

    /// <summary>
    /// The PDF version and the declaration of extensions (with the optional /Type) a later writer sets: nothing shown changes, even under a
    /// certification „no changes“ (No. 480).
    /// </summary>
    [Fact]
    public void VersionAndTypedExtensions_ValidationData()
    {
        var signed = Signed(TestPdf.Simple(1).Build(), CertificationLevel.NoChanges);
        var check = OwnSignatureOf(WithCatalog(signed, (catalog, _) =>
        {
            catalog.Set(PdfName.Version, PdfName.Get("1.7"));
            catalog.Set(PdfName.Get("Extensions"), TestPdf.Dict(("Type", TestPdf.N("Extensions")),
                ("ESIC", TestPdf.Dict(("Type", TestPdf.N("DeveloperExtensions")), ("BaseVersion", TestPdf.N("1.7")), ("ExtensionLevel", PdfInteger.Of(1))))));
        }));
        Assert.True(SignatureTexts.IsValid(check), string.Join(" | ", check.LaterDetails));
        Assert.Equal(LaterChanges.ValidationData, check.Later);
    }

    /// <summary>
    /// The logical structure (tagged PDF) set or kept up: filling in, forbidden by „no changes“ (No. 480). The name tree of appearances
    /// (comment icons): comments; a script in the name tree stays a change.
    /// </summary>
    [Fact]
    public void StructureFilling_AppearanceNamesComments_ScriptsChange()
    {
        var plain = Signed(TestPdf.Simple(1).Build(), null);
        static void Structure(PdfDictionary catalog, IncrementalUpdate update) =>
            catalog.Set(PdfName.Get("StructTreeRoot"), update.Add(TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot")))));
        var structured = OwnSignatureOf(WithCatalog(plain, Structure));
        Assert.True(SignatureTexts.IsValid(structured), string.Join(" | ", structured.LaterDetails));
        Assert.Equal(LaterChanges.FormsAndSignatures, structured.Later);
        Assert.Equal(SignatureOutcome.ChangedAfterSigning,
            OwnSignatureOf(WithCatalog(Signed(TestPdf.Simple(1).Build(), CertificationLevel.NoChanges), Structure)).Outcome);

        var icons = OwnSignatureOf(WithCatalog(plain, (catalog, update) =>
            catalog.Set(PdfName.Get("Names"), TestPdf.Dict(("AP", TestPdf.Dict(("Names", new PdfArray())))))));
        Assert.True(SignatureTexts.IsValid(icons), string.Join(" | ", icons.LaterDetails));
        Assert.Equal(LaterChanges.Comments, icons.Later);
        var script = OwnSignatureOf(WithCatalog(plain, (catalog, update) =>
            catalog.Set(PdfName.Get("Names"), TestPdf.Dict(("JavaScript", TestPdf.Dict(("Names", new PdfArray())))))));
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, script.Outcome);
    }

    /// <summary>The selection of a choice field (/I) and the text attributes (/DA) a filling program sets: filling in (No. 480).</summary>
    [Fact]
    public void ChoiceSelectionAndTextAttributes_Filling()
    {
        var signed = Signed(FormFillTests.AllKinds(), null);
        var check = OwnSignatureOf(Append(signed, (doc, update) =>
        {
            var fields = doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!;
            for (var i = 0; i < fields.Count; i++)
            {
                if (fields[i] is not PdfReference reference || doc.Resolve(reference.Id) is not PdfDictionary field) continue;
                var name = field.GetString(PdfName.Get("T"))?.ToText();
                if (name is not ("Land" or "Name")) continue;
                var copy = Copy(field);
                if (name == "Land") copy.Set(PdfName.Get("I"), new PdfArray([PdfInteger.Of(0)]));
                else copy.Set(PdfName.Get("DA"), new PdfString("/Helv 9 Tf 0 g"u8.ToArray()));
                update.Replace(reference.Id, copy);
            }
        }));
        Assert.True(SignatureTexts.IsValid(check), string.Join(" | ", check.LaterDetails));
        Assert.Equal(LaterChanges.FormsAndSignatures, check.Later);
    }

    /// <summary>
    /// The catalog written as a new object (some programs do that with each saving): compared entry by entry — the same entries are no
    /// change, a new /OpenAction is one (No. 480).
    /// </summary>
    [Fact]
    public void CatalogAsNewObject_ComparedByEntries()
    {
        var signed = Signed(TestPdf.Simple(1).Build(), null);
        byte[] NewCatalog(Action<PdfDictionary> change)
        {
            using var doc = PdfDocument.Open(signed);
            var catalog = CatalogCopy(doc);
            change(catalog);
            var size = (int)doc.Trailer.GetInteger(PdfName.Size)!;
            var prev = long.Parse(Regex.Matches(Encoding.ASCII.GetString(signed), @"startxref\s+(\d+)")[^1].Groups[1].Value);
            using var output = new MemoryStream();
            output.Write(signed);
            var offset = output.Length;
            output.Write(Encoding.ASCII.GetBytes($"\n{size} 0 obj\n"));
            output.Write(PdfSerializer.ToBytes(catalog));
            output.Write("\nendobj\n"u8);
            var xref = output.Length;
            output.Write(Encoding.ASCII.GetBytes($"xref\n{size} 1\n{offset + 1:D10} 00000 n\r\ntrailer\n<< /Size {size + 1} /Root {size} 0 R /Prev {prev} >>\nstartxref\n{xref}\n%%EOF\n"));
            return output.ToArray();
        }
        var same = OwnSignatureOf(NewCatalog(_ => { }));
        Assert.True(SignatureTexts.IsValid(same), string.Join(" | ", same.LaterDetails));
        Assert.Equal(LaterChanges.None, same.Later);
        var action = OwnSignatureOf(NewCatalog(c => c.Set(PdfName.Get("OpenAction"), TestPdf.Dict(("S", TestPdf.N("JavaScript")), ("JS", new PdfString("app.alert(1)"u8.ToArray()))))));
        Assert.Equal(SignatureOutcome.ChangedAfterSigning, action.Outcome);
    }
}
