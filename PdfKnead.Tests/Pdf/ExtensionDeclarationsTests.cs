using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The developer extensions a file declares (/Extensions, ISO 32000-2, 7.12; docs/ABLAUFPLAN.md, 3.5; docs/FEHLERGEDAECHTNIS.md, No. 488):
/// one building block for the writer and PAdES — directly in the catalog, raised and never lowered, nothing based on a later version than the
/// file's, arrays only in 2.0.
/// </summary>
public sealed class ExtensionDeclarationsTests
{
    private static readonly PdfName Extensions = PdfName.Get("Extensions");

    private static PdfDictionary Entry(string baseVersion, int level, string? url = null)
    {
        var entry = Dict(("BaseVersion", N(baseVersion)), ("ExtensionLevel", PdfInteger.Of(level)));
        if (url is not null) entry.Set(PdfName.Get("URL"), new PdfString(System.Text.Encoding.ASCII.GetBytes(url)));
        return entry;
    }

    /// <summary>A one-page file whose catalog declares <paramref name="extensions"/>, directly or as an object of its own.</summary>
    private static byte[] With(PdfDictionary extensions, bool indirect, string version = "1.7")
    {
        var pdf = Simple(1);
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(Extensions, indirect ? Ref(pdf.Add(extensions)) : extensions);
        return pdf.Build(version: version);
    }

    private static (PdfDocument Doc, PdfSaveResult Result) Saved(byte[] file, PdfSaveOptions options)
    {
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, PageItem.From(doc), output, options);
        return (PdfDocument.Open(output.ToArray(), options.Protection?.UserPassword), result);
    }

    private static PdfDictionary DeclaredIn(PdfDocument doc) => doc.Catalog!.GetDictionary(Extensions)!;

    private static int Level(PdfDictionary extensions, string prefix) => ExtensionDeclarations.LevelOf(extensions.Get(PdfName.Get(prefix)));

    // ------------------------------------------------------------ the writer

    /// <summary>
    /// /Extensions an object of its own, saved with AES-256 as 1.7: Adobe level 8 is declared beside the entries there were (before only /ADBE
    /// was left — the declaration was built on the reference of the copied catalog), and /Extensions stands directly in the catalog.
    /// </summary>
    [Fact]
    public void Aes256In17_IndirectExtensions_KeepTheirOtherEntries()
    {
        var file = With(Dict(("ESIC", Entry("1.7", 2))), indirect: true);
        var (doc, _) = Saved(file, new PdfSaveOptions { Version = "1.7", Protection = new PdfProtection("geheim", "Besitzer-1", PdfPermissions.Print) });
        using (doc)
        {
            var declared = DeclaredIn(doc);
            Assert.Equal(8, Level(declared, "ADBE"));
            Assert.Equal(2, Level(declared, "ESIC"));
            Assert.IsType<PdfDictionary>(doc.Catalog!.GetRaw(Extensions));
        }
    }

    /// <summary>A higher Adobe level stays (it declares more than PDF-Knead can judge); before it was lowered to 8.</summary>
    [Fact]
    public void Aes256In17_HigherAdbeLevel_IsKept()
    {
        var file = With(Dict(("ADBE", Entry("1.7", 11))), indirect: false);
        var (doc, _) = Saved(file, new PdfSaveOptions { Version = "1.7", Protection = new PdfProtection("geheim", "Besitzer-1", PdfPermissions.Print) });
        using (doc) Assert.Equal(11, Level(DeclaredIn(doc), "ADBE"));
    }

    /// <summary>/Extensions and its developer extensions dictionaries objects of their own: written directly (ISO 32000-2, 7.12.1).</summary>
    [Fact]
    public void Extensions_WrittenDirect()
    {
        var pdf = Simple(1);
        var esic = pdf.Add(Entry("1.7", 5));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(Extensions, Ref(pdf.Add(Dict(("ESIC", Ref(esic))))));
        var (doc, _) = Saved(pdf.Build(), new PdfSaveOptions { Version = "1.7" });
        using (doc)
        {
            var declared = Assert.IsType<PdfDictionary>(doc.Catalog!.GetRaw(Extensions));
            Assert.IsType<PdfDictionary>(declared.GetRaw(PdfName.Get("ESIC")));
            Assert.Equal(5, Level(declared, "ESIC"));
        }
    }

    /// <summary>
    /// A 2.0 file with ISO_ declarations (based on 2.0) saved as 1.7: they go (no base version above the file's, 7.12.4; no ISO_ entry below
    /// 2.0), said in the result; Adobe's 1.7 declaration stays. Saved as 2.0 they stay as they were.
    /// </summary>
    [Fact]
    public void SavedAsPdf17_ExtensionsBasedOn20_AreDropped()
    {
        var file = With(Dict(("ISO_", new PdfArray([Entry("2.0", 32001, "https://www.iso.org/standard/45874.html")])), ("ADBE", Entry("1.7", 8))), indirect: false, version: "2.0");
        var (old, result) = Saved(file, new PdfSaveOptions { Version = "1.7" });
        using (old)
        {
            var declared = DeclaredIn(old);
            Assert.False(declared.ContainsKey(PdfName.Get("ISO_")));
            Assert.Equal(8, Level(declared, "ADBE"));
            Assert.Equal(["ISO_"], result.DroppedExtensions);
        }
        var (kept, keptResult) = Saved(file, new PdfSaveOptions { Version = "2.0" });
        using (kept)
        {
            var iso = Assert.IsType<PdfArray>(DeclaredIn(kept).Get(PdfName.Get("ISO_")));
            Assert.Equal(32001, ExtensionDeclarations.LevelOf(iso.Get(0)));
            Assert.Empty(keptResult.DroppedExtensions);
        }
    }

    /// <summary>The same as PDF/A-2b (PDF 1.7): the 2.0 declarations go there as well.</summary>
    [Fact]
    public void PdfA2b_ExtensionsBasedOn20_AreDropped()
    {
        var file = With(Dict(("ISO_", new PdfArray([Entry("2.0", 32001)])), ("XYZ", Entry("2.0", 1))), indirect: true, version: "2.0");
        var (doc, result) = Saved(file, new PdfSaveOptions { PdfA = PdfAConformance.A2b });
        using (doc)
        {
            Assert.Null(doc.Catalog!.GetDictionary(Extensions));
            Assert.Equal(["ISO_", "XYZ"], result.DroppedExtensions.Order());
        }
    }

    /// <summary>
    /// An ESIC declaration (based on 1.7) in a file saved as 1.4: it goes, the base version is above the file's (7.12.4; docs/ABLAUFPLAN.md,
    /// 5.3 f — already in place since No. 488, this is the check that was still missing).
    /// </summary>
    [Fact]
    public void SavedAs14_Esic17_Dropped()
    {
        var (doc, result) = Saved(With(Dict(("ESIC", Entry("1.7", 2))), indirect: false), new PdfSaveOptions { Version = "1.4" });
        using (doc)
        {
            Assert.Null(doc.Catalog!.GetDictionary(Extensions));
            Assert.Equal(["ESIC"], result.DroppedExtensions);
        }
    }

    // ------------------------------------------------------------ the building block

    [Fact]
    public void Raise_KeepsAHigherLevel_ReplacesALowerOne()
    {
        var extensions = Dict(("ADBE", Entry("1.7", 11)), ("ESIC", Entry("1.7", 2)));
        Assert.False(ExtensionDeclarations.Raise(extensions, "ADBE", "1.7", 8, "1.7"));
        Assert.True(ExtensionDeclarations.Raise(extensions, "ESIC", "1.7", 5, "1.7"));
        Assert.Equal(11, Level(extensions, "ADBE"));
        Assert.Equal(5, Level(extensions, "ESIC"));
    }

    /// <summary>Another base version: beside it in an array in 2.0; below 2.0 one dictionary.</summary>
    [Fact]
    public void Raise_ArrayOnlyInPdf20()
    {
        var in20 = Dict(("XYZ", Entry("1.7", 3)));
        Assert.True(ExtensionDeclarations.Raise(in20, "XYZ", "2.0", 1, "2.0", "https://example.org/xyz"));
        var both = Assert.IsType<PdfArray>(in20.Get(PdfName.Get("XYZ")));
        Assert.Equal(["1.7", "2.0"], both.Select(e => ExtensionDeclarations.BaseVersionOf(e)));
        var in17 = Dict(("XYZ", Entry("1.6", 3)));
        Assert.True(ExtensionDeclarations.Raise(in17, "XYZ", "1.7", 1, "1.7", "https://example.org/xyz"));
        var one = Assert.IsType<PdfDictionary>(in17.Get(PdfName.Get("XYZ")));
        Assert.False(one.ContainsKey(PdfName.Get("URL")));   // /URL only in 2.0 files
    }

    /// <summary>ISO_ always as an array with /URL — and never below 2.0.</summary>
    [Fact]
    public void Raise_IsoAlwaysAnArray_NeverBelow20()
    {
        var extensions = new PdfDictionary();
        Assert.False(ExtensionDeclarations.Raise(extensions, "ISO_", "2.0", 32002, "1.7", "https://www.iso.org/standard/45875.html"));
        Assert.False(extensions.ContainsKey(PdfName.Get("ISO_")));
        Assert.True(ExtensionDeclarations.Raise(extensions, "ISO_", "2.0", 32002, "2.0", "https://www.iso.org/standard/45875.html"));
        var iso = Assert.IsType<PdfArray>(extensions.Get(PdfName.Get("ISO_")));
        Assert.Equal("https://www.iso.org/standard/45875.html", ((PdfDictionary)iso.Get(0)).GetString(PdfName.Get("URL"))!.ToText());
    }

    /// <summary>Below 2.0 an array is reduced to its dictionary of the highest base version that fits, the next higher level first.</summary>
    [Fact]
    public void DropAbove_ReducesAnArrayIn17()
    {
        var extensions = Dict(("XYZ", new PdfArray([Entry("1.6", 4), Entry("1.7", 2), Entry("2.0", 1)])));
        Assert.Equal(["XYZ"], ExtensionDeclarations.DropAbove(extensions, "1.7"));
        var kept = Assert.IsType<PdfDictionary>(extensions.Get(PdfName.Get("XYZ")));
        Assert.Equal(("1.7", 2), (ExtensionDeclarations.BaseVersionOf(kept), ExtensionDeclarations.LevelOf(kept)));
    }

    [Fact]
    public void Remove_ClearsEmptyEntries()
    {
        var extensions = Dict(("ISO_", new PdfArray([Entry("2.0", 32002), Entry("2.0", 32001)])), ("ESIC", Entry("1.7", 2)));
        Assert.True(ExtensionDeclarations.Remove(extensions, "ISO_", 32002));
        Assert.Single(Assert.IsType<PdfArray>(extensions.Get(PdfName.Get("ISO_"))));
        Assert.True(ExtensionDeclarations.Remove(extensions, "ISO_", 32001));
        Assert.False(extensions.ContainsKey(PdfName.Get("ISO_")));
        Assert.False(ExtensionDeclarations.Remove(extensions, "ESIC", 5));
        Assert.True(ExtensionDeclarations.Remove(extensions, "ESIC", 2));
        Assert.False(ExtensionDeclarations.Any(extensions));
    }

    // ------------------------------------------------------------ PAdES

    /// <summary>
    /// A later revision that only declares extensions — PAdES or Adobe as an array (other programs write it so even before 2.0), an ISO_
    /// array that grew — changes nothing that is shown: the signature before it stays (No. 364). One that drops an earlier declaration is a
    /// change.
    /// </summary>
    [Fact]
    public void OnlyDeclarations_ArraysAndAGrowingIsoArray()
    {
        static PdfDictionary Catalog(PdfDictionary extensions) => Dict(("Type", N("Catalog")), ("Extensions", extensions));
        var before = Catalog(Dict(("ESIC", new PdfArray([Entry("1.7", 2)])), ("ISO_", new PdfArray([Entry("2.0", 32001)]))));
        Assert.True(PadesExtension.OnlyDeclarations(before,
            Catalog(Dict(("ESIC", new PdfArray([Entry("1.7", 2)])), ("ISO_", new PdfArray([Entry("2.0", 32001), Entry("2.0", 32002)])), ("ADBE", Entry("1.7", 8))))));
        Assert.False(PadesExtension.OnlyDeclarations(before,
            Catalog(Dict(("ESIC", new PdfArray([Entry("1.7", 2)])), ("ISO_", new PdfArray([Entry("2.0", 32002)]))))));
    }

    /// <summary>
    /// The levels of ISO_ name different specifications: 32003 declared does not declare 32002 — it is added beside it, and 32003 stays; the
    /// same level again changes nothing (docs/FEHLERGEDAECHTNIS.md, No. 536). Before 32003 counted as „32002 or higher“, and a new entry would
    /// have pushed out every other one of base version 2.0.
    /// </summary>
    [Fact]
    public void Raise_IsoLevelsAreSpecifications_KeptBesideEachOther()
    {
        var extensions = Dict(("ISO_", new PdfArray([Entry("2.0", 32003)])));
        Assert.True(ExtensionDeclarations.Raise(extensions, "ISO_", "2.0", 32002, "2.0", "https://www.iso.org/standard/45875.html", ":2022"));
        var iso = Assert.IsType<PdfArray>(extensions.Get(PdfName.Get("ISO_")));
        Assert.Equal([32003, 32002], Enumerable.Range(0, iso.Count).Select(i => ExtensionDeclarations.LevelOf(iso.Get(i))));
        Assert.False(ExtensionDeclarations.Raise(extensions, "ISO_", "2.0", 32002, "2.0"));
    }
    /// <summary>
    /// ISO_ declarations as the tables of ISO/TS 32003 and 32004 (clause 4, Table 1) fix them: a new entry carries /Type /DeveloperExtensions;
    /// an entry of the same level with another revision or URL is replaced (before, it stayed because only the level was compared); one with
    /// the right values stays as it is (docs/FEHLERGEDAECHTNIS.md, No. 1103).
    /// </summary>
    [Fact]
    public void Raise_IsoEntryWithOtherValues_Replaced_NewEntryTyped()
    {
        const string url = "https://www.iso.org/standard/45876.html";
        var extensions = new PdfDictionary();
        extensions.Set(PdfName.Get("ISO_"), new PdfArray([Dict(("BaseVersion", N("2.0")), ("ExtensionLevel", PdfInteger.Of(32003)),
            ("ExtensionRevision", new PdfString(":2019"u8.ToArray())))]));
        Assert.True(ExtensionDeclarations.Raise(extensions, "ISO_", "2.0", 32003, "2.0", url, ":2023"));
        var entry = Assert.Single(ExtensionDeclarations.EntriesOf(extensions, "ISO_"));
        Assert.Equal("DeveloperExtensions", entry.GetName(PdfName.Type)!.Value);
        Assert.Equal(":2023"u8.ToArray(), entry.GetString(PdfName.Get("ExtensionRevision"))!.Bytes);
        Assert.Equal(System.Text.Encoding.ASCII.GetBytes(url), entry.GetString(PdfName.Get("URL"))!.Bytes);
        Assert.False(ExtensionDeclarations.Raise(extensions, "ISO_", "2.0", 32003, "2.0", url, ":2023"));
    }
}
